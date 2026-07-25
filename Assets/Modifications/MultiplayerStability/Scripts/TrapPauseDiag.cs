// Trap/pause command diagnostic and null-IK containment.
//
// During trap auto-pause, AbstractUnitCommand.OnRun (:830) calls ForceLookAt before setting DidRun=true.
// ForceRotateToDesired (AbstractUnitEntity.cs:658) writes the simulation orientation, updates the view
// rotation, and, for a visible unit while paused, calls
// View.IkController.GrounderIk.ResetPosition(). View visibility and the IK object graph are client-local.
// A missing IK object throws after the simulation write and aborts UnitCommandBuffer.Tick mid-batch.
// Retried command residue can then differ between peers. Captures recorded 72 versus 10 null-reference
// exceptions, 514 versus 107 residual command exceptions, matching affected entity sets, and no RNG
// difference.
//
// The diagnostic records the first 80 paused-window calls per accepted Pause episode and every exception.
// Each record uses (networkTick, UniqueId, seq), where seq is a per-tick per-unit ordinal. Cross-peer
// analysis compares records by this key; exception counts alone are insufficient. The budget resets in
// Game.HandleGameModeChanged when newMode==Pause, with tick regression as a load fallback.
//
// Since v0.8.26, a multiplayer-only prefix preserves the orientation write and view rotation but treats
// a successfully read null IkController or GrounderIk as an omitted reset. Missing reflected members
// disable containment and return to vanilla behavior. ResetPosition exceptions are unwrapped and
// rethrown once. The patch does not modify UnitCommandBuffer or suppress unrelated exceptions.
//
// IK types are read reflectively because their assembly is not referenced by the template.
// Diagnostic: subset-safe. Containment: exact parity required.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.GameModes;
using Kingmaker.Mechanics.Entities;
using Kingmaker.Networking;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(AbstractUnitEntity), nameof(AbstractUnitEntity.ForceRotateToDesired))]
    internal static class ForceRotateToDesired_Diag_Patch
    {
        // Budget is per pause EPISODE: a session-lifetime cap would exhaust on earlier storms and erase the
        // successful peer's comparison evidence for the decisive episode (the 0.8.19 host
        // had 57 throwing calls before its first trap). The episode boundary is the accepted transition:
        // the budget resets at the ACCEPTED game-mode transition -- HandleGameModeChanged with newMode ==
        // Pause (StartMode is only a REQUEST that can be rejected or enqueued as a command, so a StartMode
        // prefix could reset on rejected/duplicate requests and misalign across peers); tick < lastTick
        // (save/reload regression) is the fallback reset.
        internal const int BreadcrumbCapPerEpisode = 80;
        internal static int s_breadcrumbs;
        internal static readonly Dictionary<string, int> s_perTickSeq = new Dictionary<string, int>();
        private static int s_seqTick = int.MinValue / 2;
        private static int s_lastTick = int.MinValue / 2;

        // Per-(tick, unit) invocation ordinal via a per-tick dictionary -- a last-call comparison collides
        // on interleaved batching (A,B,A in one tick gave A:0,B:0,A:0; UnitCommandBuffer.Tick produces
        // exactly that shape). Assigned ONLY to paused-window calls (the diagnostic key space) so unpaused
        // traffic cannot perturb the ordinals; computed in the prefix, carried to the finalizer via __state.
        private static void Prefix(AbstractUnitEntity __instance, out int __state)
        {
            __state = -1;
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return;
                int tick = Game.Instance.RealTimeController.CurrentNetworkTick;
                if (tick < s_lastTick)
                    s_breadcrumbs = 0;                          // tick regression (save/reload): fallback reset
                s_lastTick = tick;
                if (!Game.Instance.IsPaused)
                    return;                                     // only the paused window is the suspect class
                if (tick != s_seqTick)
                {
                    s_perTickSeq.Clear();
                    s_seqTick = tick;
                }
                string unit = SafeName(__instance);
                int seq;
                s_perTickSeq.TryGetValue(unit, out seq);
                s_perTickSeq[unit] = seq + 1;
                __state = seq;
                if (s_breadcrumbs >= BreadcrumbCapPerEpisode)
                    return;
                s_breadcrumbs++;
                MultiplayerStabilityMain.Log("[TrapDiag] ForceRotateToDesired(paused) unit=" + unit
                    + " tick=" + tick + " seq=" + seq
                    + " view=" + (__instance.View != null)
                    + (__instance.View != null
                        ? " visible=" + __instance.View.IsVisible
                          + " vt=" + (__instance.View.ViewTransform != null)
                          + " ik=" + IkState(__instance)
                        : "")
                    + (s_breadcrumbs == BreadcrumbCapPerEpisode ? " (episode cap reached; exceptions still logged)" : ""));
            }
            catch (Exception)
            {
                // log-only diagnostic: never interfere
            }
        }

        // Exceptions are the divergence event itself -- always logged, ALWAYS rethrown unchanged.
        private static Exception Finalizer(AbstractUnitEntity __instance, int __state, Exception __exception)
        {
            try
            {
                if (__exception != null && NetworkingManager.IsMultiplayer)
                {
                    MultiplayerStabilityMain.Log("[TrapDiag][EXC] ForceRotateToDesired threw AFTER the sim orientation write: unit="
                        + SafeName(__instance)
                        + " tick=" + Game.Instance.RealTimeController.CurrentNetworkTick + " seq=" + __state
                        + " paused=" + Game.Instance.IsPaused
                        + " view=" + (__instance.View != null)
                        + (__instance.View != null
                            ? " visible=" + __instance.View.IsVisible
                              + " vt=" + (__instance.View.ViewTransform != null)
                              + " ik=" + IkState(__instance)
                            : "")
                        + " -> " + __exception.GetType().Name + ": " + __exception.Message);
                }
            }
            catch (Exception)
            {
            }
            return __exception;                                 // unchanged: diagnosis, not containment
        }

        private static string SafeName(AbstractUnitEntity unit)
        {
            try
            {
                return unit != null ? unit.UniqueId : "null";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        // CONTAINMENT (v0.8.26, hardened v0.8.27): reimplement ForceRotateToDesired in MP with a null-safe
        // IK reset. Runs at LOWER priority than the diagnostic prefix above (so breadcrumbs still record
        // every invocation) and returns false to skip vanilla; the diagnostic finalizer still wraps
        // everything, so any exception that escapes is logged as [EXC] with full context before rethrow.
        // v0.8.27 failure-routing contract (review-hardened):
        //   - reflection DRIFT (member lookup fails after a game update) -> vanilla, latched, [ERR] once --
        //     drift must never masquerade as the known defect;
        //   - successfully READ null IkController/GrounderIk -> the known defect -> contained + logged
        //     (logging is strictly best-effort: a logger throw cannot re-enable the vanilla NRE);
        //   - a real ResetPosition() failure -> surfaces ONCE, unwrapped, past our fail-open (no vanilla
        //     rerun of a non-idempotent reset);
        //   - anything else unexpected in the reimpl -> vanilla (idempotent writes only at that point).
        // The orientation FieldRef resolves in Prepare(): if it fails (rename), the patch DECLINES and
        // vanilla stands -- a static-initializer throw would instead have broken the method entirely.
        [HarmonyPatch(typeof(AbstractUnitEntity), nameof(AbstractUnitEntity.ForceRotateToDesired))]
        [HarmonyPriority(Priority.Low)]
        internal static class ForceRotateToDesired_Containment_Patch
        {
            private static AccessTools.FieldRef<AbstractUnitEntity, float> s_orientation;
            private static bool s_loggedActive;
            private static bool s_reflectionDrift;
            private static int s_containedCount;

            private static bool Prepare()
            {
                try
                {
                    s_orientation = AccessTools.FieldRefAccess<AbstractUnitEntity, float>("m_Orientation");
                    return s_orientation != null;
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[TrapFix][ERR] m_Orientation not resolvable; containment declined: "
                        + e.Message);
                    return false;                                // patch not applied at all
                }
            }

            private static bool Prefix(AbstractUnitEntity __instance)
            {
                if (!MultiplayerCompatibility.SimulationFixesEnabled || s_reflectionDrift)
                    return true;                                 // vanilla gate, or drift-latched
                object grounder = null;
                MethodInfo reset = null;
                try
                {
                    // Vanilla body, faithfully -- the SIM write always happens first and unconditionally.
                    s_orientation(__instance) = __instance.DesiredOrientation;
                    bool isPaused = Game.Instance.IsPaused;
                    var view = __instance.View;
                    if (view != null && (isPaused || !view.IsVisible))
                    {
                        // Deliberately unguarded like vanilla (containment scope is the IK reset ONLY):
                        view.ViewTransform.rotation = UnityEngine.Quaternion.Euler(0f, __instance.Orientation, 0f);
                        if (isPaused && view.IsVisible)
                        {
                            // Metadata must resolve (else: drift -> vanilla). Only a successfully READ null
                            // value is the known defect to contain.
                            var ikProp = AccessTools.Property(view.GetType(), "IkController");
                            if (ikProp == null)
                                return Drift("IkController property");
                            var ik = ikProp.GetValue(view);
                            if (ik == null)
                            {
                                LogContained(__instance, "noik");
                            }
                            else
                            {
                                var grounderProp = AccessTools.Property(ik.GetType(), "GrounderIk");
                                if (grounderProp == null)
                                    return Drift("GrounderIk property");
                                var g = grounderProp.GetValue(ik);
                                if (g == null)
                                {
                                    LogContained(__instance, "nogrounder");
                                }
                                else
                                {
                                    reset = g.GetType().GetMethod("ResetPosition", Type.EmptyTypes);
                                    if (reset == null)
                                        return Drift("ResetPosition()");
                                    grounder = g;                // invoke OUTSIDE the fail-open try below
                                }
                            }
                        }
                    }
                    LogActiveOnce();                             // best-effort: must not reach the fail-open
                }
                catch (Exception)
                {
                    // Unexpected failure in the reimpl BEFORE any IK invoke: fall back to vanilla (the
                    // writes so far are idempotent); whatever threw recurs there and surfaces normally.
                    return true;
                }
                if (grounder != null && reset != null)
                {
                    // A real ResetPosition failure is unrelated to null-containment and must surface ONCE:
                    // unwrap and rethrow PAST our fail-open (no vanilla rerun of a non-idempotent reset).
                    // The diag finalizer above still logs it with full context.
                    try
                    {
                        reset.Invoke(grounder, null);
                    }
                    catch (TargetInvocationException tie)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo
                            .Capture(tie.InnerException ?? tie).Throw();
                    }
                }
                return false;                                    // vanilla skipped; batch bookkeeping proceeds
            }

            private static bool Drift(string member)
            {
                s_reflectionDrift = true;
                MultiplayerStabilityMain.LogNoThrow(
                    "[TrapFix][ERR] reflection drift (" + member
                    + " not found); containment disabled, vanilla stands.");
                return true;                                     // this call and all future calls: vanilla
            }

            // Strictly best-effort: nothing thrown here may reach the containment path -- a logger failure
            // re-enabling the vanilla NRE would defeat the fix. This includes the activation log.
            private static void LogActiveOnce()
            {
                try
                {
                    if (s_loggedActive)
                        return;
                    MultiplayerStabilityMain.Log("[TrapFix] Containment active -- a missing paused-facing IK graph no longer aborts the command batch with an NRE.");
                    s_loggedActive = true;
                }
                catch (Exception)
                {
                }
            }

            // Strictly best-effort: nothing thrown here may reach the containment path -- a logger or tick
            // failure re-enabling the vanilla NRE would defeat the fix.
            private static void LogContained(AbstractUnitEntity unit, string kind)
            {
                try
                {
                    if (s_containedCount >= 200)
                        return;
                    s_containedCount++;
                    MultiplayerStabilityMain.Log("[TrapFix] Contained missing-IK reset: unit="
                        + (unit != null ? unit.UniqueId : "null")
                        + " tick=" + Game.Instance.RealTimeController.CurrentNetworkTick
                        + " " + kind
                        + (s_containedCount == 200 ? " (containment log cap reached)" : ""));
                }
                catch (Exception)
                {
                }
            }
        }

        // Exact episode boundary: the budget resets at the ACCEPTED transition -- Game.HandleGameModeChanged
        // (private; the point every peer's synchronized mode change actually executes) with newMode == Pause.
        // NOT Game.StartMode: that is a request that can be rejected (already-active, game-over) or enqueued
        // as a StartGameModeCommand, so a StartMode reset could fire on rejected/duplicate requests and
        // misalign across peers. The per-tick ordinal map is also cleared here so an
        // episode's keys start clean.
        [HarmonyPatch(typeof(Game), "HandleGameModeChanged",
            typeof(GameModeType), typeof(GameModeType))]
        internal static class PauseEpisode_Reset_Patch
        {
            private static void Prefix(GameModeType oldMode, GameModeType newMode)
            {
                try
                {
                    if (newMode == GameModeType.Pause)
                    {
                        s_breadcrumbs = 0;
                        s_perTickSeq.Clear();
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        // IkController/GrounderIk types are outside the template reference set -- null-chain reflectively.
        private static string IkState(AbstractUnitEntity unit)
        {
            try
            {
                var view = unit.View;
                var ik = AccessTools.Property(view.GetType(), "IkController")?.GetValue(view);
                if (ik == null)
                    return "noik";
                var grounder = AccessTools.Property(ik.GetType(), "GrounderIk")?.GetValue(ik);
                return grounder == null ? "nogrounder" : "ok";
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}

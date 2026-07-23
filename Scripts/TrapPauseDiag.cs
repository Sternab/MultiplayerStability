// Trap/pause command-lifecycle DIAGNOSTIC -- log-only (capture 0.8.19, Codex round 23; two independent
// sceneEntities episodes, party members only, RNG identical).
//
// The class (decompile-verified): trap detection auto-pauses; while paused, AbstractUnitCommand.OnRun
// (:830) calls Executor.ForceLookAt(Target.Point) BEFORE setting DidRun = true. ForceRotateToDesired
// (AbstractUnitEntity.cs:658) writes SIM orientation (m_Orientation = DesiredOrientation) and THEN touches
// client-local view state: ViewTransform.rotation, and -- when paused && View.IsVisible -- the IK chain
// (View.IkController.GrounderIk.ResetPosition()). View.IsVisible and the IK object graph are CLIENT-LOCAL,
// so the tail can throw AFTER the sim write. (Round-30 correction: both peers often throw on the SAME
// first keyed invocation -- the divergence is DOWNSTREAM: the shared NRE aborts UnitCommandBuffer.Tick
// mid-batch and the residual commands/handles retry differently per peer.) Either way the party members'
// command lifecycle and orientation diverge -> the captured sceneEntities forks (Argenta+player @20:38,
// Cassia+Kibellah @21:32). Same storm exists in 0.6.4-era logs: a longstanding vanilla defect, not a mod
// regression.
//
// This diagnostic does NOT change behavior (exceptions are logged and rethrown unchanged). It exists to
// give the next capture per-machine evidence of WHERE the asymmetry enters: the FIRST 80 paused-window
// ForceRotateToDesired calls of each pause episode log unit + view/visibility/transform/IK state, and ALL
// exceptions log full context regardless of the budget.
// ACCEPTANCE CRITERION for the two-sided diff (Codex rounds 24-26 -- both 0.8.19 peers threw; mere presence
// of [EXC] lines proves nothing): every paused-window line carries a UNIQUE key (networkTick, UniqueId, seq)
// -- seq is a per-tick per-unit counter (dictionary, not last-call comparison, so interleaved A,B,A batching
// cannot collide) -- and the comparison is a keyed diff across peers: decisive = a key that THREW on one
// peer while the same key logged a successful breadcrumb (or different state) on the other. Successful-call
// breadcrumbs are first-class evidence, which is why the budget is per pause EPISODE (reset at the ACCEPTED
// game-mode transition, HandleGameModeChanged newMode==Pause -- StartMode is only a request that can be
// rejected or deferred), never per session: a lifetime cap exhausted before the decisive window would erase
// the successful peer's counterpart, making "succeeded" indistinguishable from "never called." The
// containment fix (guarding the view tail so bookkeeping completes) comes AFTER this evidence, as its own
// reviewed change. Candidate sibling seams (movement startup, UnitFollowUnitController.ShouldAct's
// View.MovementAgent.WantsToMove read) are deliberately not instrumented yet -- scope stays on the proven
// site.
//
// IK objects' types live outside the template reference assemblies -- read reflectively (null-checks only).
// The DIAGNOSTIC half is log-only. Since v0.8.26 this file also carries the CONTAINMENT (capture 0.8.xx,
// Codex round 30 -- evidence conclusive: 72-vs-10 trap NREs, 514-vs-107 'Cmd is already set' residue, forks
// isolated to the touched units, zero RNG/creation differences; three trap storms immediately preceded room
// disconnections). IMPORTANT CORRECTION from that capture: both peers often threw on the SAME first keyed
// invocation -- the divergence is DOWNSTREAM: the shared NRE aborts UnitCommandBuffer.Tick mid-batch and the
// residual commands/handles retry differently per peer. So the cure is containment of the null-IK reset (the
// NRE no longer aborts the batch), NOT symmetry repair: the reimplementing prefix preserves the sim orientation
// write and the vanilla view-rotation behavior exactly, and ONLY the paused, visible-unit IK reset becomes
// null-safe (missing IkController/GrounderIk -> skip, logged). Every unrelated exception still surfaces:
// an unexpected throw in the reimpl falls back to vanilla (idempotent writes), where it recurs naturally.
// Do NOT patch UnitCommandBuffer or swallow NREs broadly (Codex's explicit boundary).
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
        // successful peer's comparison evidence for the decisive episode (Codex round 24 -- the 0.8.19 host
        // had 57 throwing calls before its first trap). The episode boundary is EXACT (Codex rounds 25-26):
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
        // v0.8.27 failure-routing contract (Codex round 31):
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
                    MultiplayerStabilityMain.Log("[TrapFix][ERR] m_Orientation not resolvable -- containment declined, vanilla stands: " + e.Message);
                    return false;                                // patch not applied at all
                }
            }

            private static bool Prefix(AbstractUnitEntity __instance)
            {
                if (!NetworkingManager.IsMultiplayer || s_reflectionDrift)
                    return true;                                 // solo, or drift-latched: vanilla exactly
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
                try
                {
                    MultiplayerStabilityMain.Log("[TrapFix][ERR] reflection drift (" + member + " not found) -- containment disabled, vanilla stands.");
                }
                catch (Exception)
                {
                }
                return true;                                     // this call and all future calls: vanilla
            }

            // Strictly best-effort: nothing thrown here may reach the containment path -- a logger failure
            // re-enabling the vanilla NRE would defeat the fix (Codex rounds 31-32; round 32 caught the
            // activation log still inside the fail-open try, one branch from the same self-defeat).
            private static void LogActiveOnce()
            {
                try
                {
                    if (s_loggedActive)
                        return;
                    s_loggedActive = true;
                    MultiplayerStabilityMain.Log("[TrapFix] Containment active -- a missing paused-facing IK graph no longer aborts the command batch with an NRE.");
                }
                catch (Exception)
                {
                }
            }

            // Strictly best-effort: nothing thrown here may reach the containment path -- a logger or tick
            // failure re-enabling the vanilla NRE would defeat the fix (Codex round 31).
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
        // misalign across peers (Codex round 26). The per-tick ordinal map is also cleared here so an
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

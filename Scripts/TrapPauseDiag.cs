// Trap/pause command-lifecycle DIAGNOSTIC -- log-only (capture 0.8.19, Codex round 23; two independent
// sceneEntities episodes, party members only, RNG identical).
//
// The class (decompile-verified): trap detection auto-pauses; while paused, AbstractUnitCommand.OnRun
// (:830) calls Executor.ForceLookAt(Target.Point) BEFORE setting DidRun = true. ForceRotateToDesired
// (AbstractUnitEntity.cs:658) writes SIM orientation (m_Orientation = DesiredOrientation) and THEN touches
// client-local view state: ViewTransform.rotation, and -- when paused && View.IsVisible -- the IK chain
// (View.IkController.GrounderIk.ResetPosition()). View.IsVisible and the IK object graph are CLIENT-LOCAL,
// so on one machine the tail can throw (or simply take a different branch) AFTER the sim write; a throw
// prevents DidRun/command bookkeeping from completing on THAT machine only -> the party members' command
// lifecycle and orientation diverge -> the captured sceneEntities forks (Argenta+player @20:38,
// Cassia+Kibellah @21:32). Same storm exists in 0.6.4-era logs: a longstanding vanilla defect, not a mod
// regression.
//
// This diagnostic does NOT change behavior (exceptions are logged and rethrown unchanged). It exists to
// give the next capture per-machine evidence of WHERE the asymmetry enters: every ForceRotateToDesired
// call while paused logs the unit + view/visibility/transform/IK state; every exception logs full context.
// ACCEPTANCE CRITERION for the two-sided diff (Codex round 24 -- both 0.8.19 peers threw; mere presence of
// [EXC] lines proves nothing): key every line by (networkTick, UniqueId) and compare the KEYED SETS across
// peers -- the decisive result is a key that THREW on one peer while the same key logged a successful
// breadcrumb (or state difference) on the other. That is why successful-call breadcrumbs are first-class
// evidence and the budget is per pause EPISODE, not per session: a session-lifetime cap exhausted before
// the decisive window would erase the successful peer's counterpart, making "succeeded" indistinguishable
// from "never called." The containment fix (guarding the view tail so bookkeeping completes) comes AFTER
// this evidence, as its own reviewed change. Candidate sibling seams (movement startup,
// UnitFollowUnitController.ShouldAct's View.MovementAgent.WantsToMove read) are deliberately not
// instrumented yet -- scope stays on the proven site.
//
// IK objects' types live outside the template reference assemblies -- read reflectively (null-checks only).
// Log-only -> subset-safe; MP-gated. Breadcrumbs: the FIRST 80 paused-window calls of each pause episode
// (episode = an actual GameModeType.Pause StartMode transition, with tick-regression as the save/reload
// fallback); exceptions are always logged regardless of the budget. Every line carries a per-(tick, unit)
// invocation ordinal (seq) because one unit can run multiple commands in a single simulation tick -- the
// two-sided comparison must treat records as ordered multisets keyed (tick, unit, seq), or an upstream
// call-count divergence would masquerade as throw-versus-success.
using System;
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
        // had 57 throwing calls before its first trap). The episode boundary is EXACT (Codex round 25): the
        // budget resets on the real GameModeType.Pause StartMode transition (patch below), so dense
        // back-to-back traps each get a fresh budget; tick < lastTick (save/reload regression) is the
        // fallback reset.
        internal const int BreadcrumbCapPerEpisode = 80;
        internal static int s_breadcrumbs;
        private static int s_lastTick = int.MinValue / 2;
        private static string s_lastUnit;
        private static int s_seq;

        // Per-(tick, unit) invocation ordinal: one unit can run several commands in one simulation tick, so
        // (tick, unit) alone is not a unique key. Computed in the prefix, carried to the finalizer via
        // __state so the breadcrumb and any [EXC] line for the same invocation share the same seq.
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
                string unit = SafeName(__instance);
                if (tick == s_lastTick && unit == s_lastUnit)
                    s_seq++;
                else
                    s_seq = 0;
                s_lastTick = tick;
                s_lastUnit = unit;
                __state = s_seq;
                if (!Game.Instance.IsPaused)
                    return;                                     // only the paused window is the suspect class
                if (s_breadcrumbs >= BreadcrumbCapPerEpisode)
                    return;
                s_breadcrumbs++;
                MultiplayerStabilityMain.Log("[TrapDiag] ForceRotateToDesired(paused) unit=" + unit
                    + " tick=" + tick + " seq=" + s_seq
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

        // Exact episode boundary: the budget resets when the Pause game mode actually STARTS -- dense
        // back-to-back trap pauses each get a fresh budget (the 5s-gap heuristic could not separate them,
        // and a save/reload tick regression made the gap negative and never reset; Codex round 25).
        [HarmonyPatch(typeof(Game), nameof(Game.StartMode), typeof(GameModeType))]
        internal static class PauseEpisode_Reset_Patch
        {
            private static void Prefix(GameModeType type)
            {
                try
                {
                    if (type == GameModeType.Pause)
                        s_breadcrumbs = 0;
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

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
// call while paused logs the unit + view/visibility/IK state; every exception logs full context. The
// containment fix (guarding the view tail so bookkeeping completes) comes AFTER this evidence, as its own
// reviewed change. Candidate sibling seams (movement startup, UnitFollowUnitController.ShouldAct's
// View.MovementAgent.WantsToMove read) are deliberately not instrumented yet -- scope stays on the proven
// site.
//
// IK objects' types live outside the template reference assemblies -- read reflectively (null-checks only).
// Log-only -> subset-safe; MP-gated; paused-context breadcrumbs capped per session, exceptions always logged.
using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Mechanics.Entities;
using Kingmaker.Networking;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(AbstractUnitEntity), nameof(AbstractUnitEntity.ForceRotateToDesired))]
    internal static class ForceRotateToDesired_Diag_Patch
    {
        private const int BreadcrumbCap = 80;
        private static int s_breadcrumbs;

        private static void Prefix(AbstractUnitEntity __instance)
        {
            try
            {
                if (!NetworkingManager.IsMultiplayer || !Game.Instance.IsPaused)
                    return;                                     // only the paused window is the suspect class
                if (s_breadcrumbs >= BreadcrumbCap)
                    return;
                s_breadcrumbs++;
                MultiplayerStabilityMain.Log("[TrapDiag] ForceRotateToDesired(paused) unit=" + SafeName(__instance)
                    + " tick=" + Game.Instance.RealTimeController.CurrentNetworkTick
                    + " view=" + (__instance.View != null)
                    + (__instance.View != null ? " visible=" + __instance.View.IsVisible + " ik=" + IkState(__instance) : "")
                    + (s_breadcrumbs == BreadcrumbCap ? " (breadcrumb cap reached; exceptions still logged)" : ""));
            }
            catch (Exception)
            {
                // log-only diagnostic: never interfere
            }
        }

        // Exceptions are the divergence event itself -- always logged, ALWAYS rethrown unchanged.
        private static Exception Finalizer(AbstractUnitEntity __instance, Exception __exception)
        {
            try
            {
                if (__exception != null && NetworkingManager.IsMultiplayer)
                {
                    MultiplayerStabilityMain.Log("[TrapDiag][EXC] ForceRotateToDesired threw AFTER the sim orientation write: unit="
                        + SafeName(__instance)
                        + " tick=" + Game.Instance.RealTimeController.CurrentNetworkTick
                        + " paused=" + Game.Instance.IsPaused
                        + " view=" + (__instance.View != null)
                        + (__instance.View != null ? " visible=" + __instance.View.IsVisible + " ik=" + IkState(__instance) : "")
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

// Proving Ground interrupt-turn ordering fix.
//
// Paired three-player v0.9.1 captures recorded the same permanent scene-entity fork twice:
//   - an interrupt turn began for Kibellah or Abelard;
//   - every peer consumed the same RNG and created the same seven
//     EaglePet_ProvingGround_Ally_Bonus_Buff facts;
//   - only the interrupt-turn unit's final entity hash differed, with a different value on each peer.
//
// The feature's combat-start ContextActionOnAllUnitsInCombat enumerates AllBaseUnits without sorting
// and applies a marker buff to every ally. Each marker subscribes an ExtraTurnWithReasonTrigger with
// AnyUnitTurns and ActionsOnTheTurnOwner enabled. The event bus invokes those markers in registration
// order, and each applies the same bonus buff with Stacking.Replace. Buff creation clones and hashes
// the marker's parent context, so the last marker to execute determines the final surviving context.
// Different AllBaseUnits order therefore produces different final hashes without changing RNG or
// fact counts.
//
// The patch targets only the convicted ContextActionOnAllUnitsInCombat element and sorts its fully
// filtered target list with the engine's own UniqueId comparer. All seven vanilla applications remain,
// including their late-combat-join behavior; only their registration order becomes canonical. Solo,
// unresolved, and mixed-build sessions retain vanilla order.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.Controllers.Optimization;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Mechanics.Actions;

namespace MultiplayerStability
{
    internal static class ProvingGroundOrderFix
    {
        private const string ProvingGroundMarkerActionGuid =
            "cd95a072272a4ad08b2e8f4593075c6e";

        [HarmonyPatch(typeof(ContextActionOnAllUnitsInCombat), "FilterTargetsByStat")]
        internal static class FilterTargetsByStat_Patch
        {
            private static bool s_loggedActive;

            private static void Postfix(
                ContextActionOnAllUnitsInCombat __instance,
                List<BaseUnitEntity> __result)
            {
                if (!MultiplayerCompatibility.SimulationFixesEnabled || __result == null)
                    return;

                string actionGuid;
                try
                {
                    actionGuid = __instance?.AssetGuid;
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[ProvingGroundFix][ERR] Could not identify the all-units action; "
                        + "vanilla order remains active: " + e.Message);
                    return;
                }

                if (!string.Equals(
                    actionGuid,
                    ProvingGroundMarkerActionGuid,
                    StringComparison.Ordinal))
                {
                    return;
                }

                try
                {
                    if (__result.Count > 1)
                        __result.Sort(MechanicEntityHelper.ByIdComparison);
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[ProvingGroundFix][ERR] Could not sort marker targets; "
                        + "vanilla order remains active: " + e.Message);
                    return;
                }

                LogActiveOnce();
            }

            private static void LogActiveOnce()
            {
                if (s_loggedActive)
                    return;

                try
                {
                    MultiplayerStabilityMain.Log(
                        "[ProvingGroundFix] Active; marker targets sort by UniqueId.");
                    s_loggedActive = true;
                }
                catch
                {
                    // Retry the presence signal on the next Proving Ground combat.
                }
            }
        }
    }
}

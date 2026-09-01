// Canonical ordering for ContextActionOnAllUnitsInCombat.
//
// Rogue Trader builds this action's fully filtered target list from State.AllBaseUnits and then
// executes its nested actions in that list's insertion order. Entity-pool insertion order depends
// on prior spawn, load, and view-lifecycle history, so equal target membership can still produce a
// different action order on each peer. Order is observable when actions allocate fact IDs, replace
// buffs, register event subscribers, or choose a random target by list index.
//
// The original narrow version of this patch covered the Cyber Eagle's Proving Ground marker pass:
// paired three-player captures showed the same seven applications and RNG draws but a different
// surviving replacement-buff context. Paired v0.9.5 captures in the Lex Imperialis palace exposed
// two more buff forks that point back to this primitive. VC2_Mercenary_FocusOnTarget_Link_Buff ended
// with a different recipient hash, while UndertakerVindictorFlamer_EnemyBuff advanced GlobalUuid a
// different number of times. The shipped blueprint pack places both DLC buff families under
// ContextActionOnAllUnitsInCombat; post-fix palace validation is still required.
//
// Fix: sort every fully filtered target list with the engine's own UniqueId comparer before random
// selection or sequential execution. The member set, filters, actions, and number of iterations are
// unchanged. Canonical order can also stabilize downstream condition results when earlier actions
// affect later targets. It cannot repair peers that already disagree about target membership.
// Solo, unresolved, and mixed-build sessions retain vanilla order.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.Controllers.Optimization;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Mechanics.Actions;

namespace MultiplayerStability
{
    internal static class AllUnitsCombatOrderFix
    {
        [HarmonyPatch(typeof(ContextActionOnAllUnitsInCombat), "FilterTargetsByStat")]
        internal static class FilterTargetsByStat_Patch
        {
            private static bool s_loggedActive;

            private static void Postfix(List<BaseUnitEntity> __result)
            {
                if (!MultiplayerCompatibility.SimulationFixesEnabled || __result == null)
                    return;

                try
                {
                    if (__result.Count > 1)
                        __result.Sort(MechanicEntityHelper.ByIdComparison);
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[AllUnitsOrderFix][ERR] Could not sort action targets; "
                        + "caller receives the current list order: " + e.Message);
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
                        "[AllUnitsOrderFix] Active; filtered combat-unit targets sort by UniqueId.");
                    s_loggedActive = true;
                }
                catch
                {
                    // Retry the presence signal on the next all-units action.
                }
            }
        }
    }
}

// Action-bar role-event spam fix -- the 600 MB log storm (tester incident, 2026-07-11; chain verified in
// the decompile, counts from retained captures: 18,036 identical exception stacks in the 0.6.9 client log).
//
// When a co-op player leaves, PlayerRole.OnPlayerLeftRoom (PlayerRole.cs:112) raises
// INetRoleSetHandler.HandleRoleSet(entityId) for EVERY controlled entity whose role group contained the
// leaver (~1,500 on a live save). ActionBarSlotVM.HandleRoleSet (ActionBarSlotVM.cs:560) then IGNORES the
// entityId parameter and unconditionally refreshes its slot -- so all 12 action-bar slots refresh ~1,500
// times each, and any slot whose MechanicActionBarSlot has a missing Unit throws NullReferenceException
// (MechanicActionBarSlot.IsDisabled/IsPossibleActive dereference this.Unit) on EVERY one of those refreshes.
// 1,500 x 12 full exception stacks in seconds; GameLogFull has no size cap (LogSinkFactory.cs:29 passes
// int.MaxValue), so the log grows unbounded -- testers reported 600 MB files. The storm also hitches the
// disconnect/recovery path and buries the diagnostic evidence we actually need around a player-leave.
//
// Fix (narrow, UI-only, no simulation contact): prefix on ActionBarSlotVM.HandleRoleSet --
//   1. unitless slot -> skip the refresh entirely (there is nothing to update; this kills the NRE source);
//   2. event about a DIFFERENT entity than this slot's unit -> skip (kills the ~1,500x amplification; the
//      slot still refreshes when ITS unit's role actually changes, which is the handler's purpose).
// Vanilla behaviour is preserved exactly for the one event that matters to each slot. Deliberately NOT a
// global exception suppressor -- real exceptions elsewhere keep their full context (Codex's line, agreed).
// Role events only exist in multiplayer, but the prefix defers to vanilla outside MP anyway; fail-open.
using System;
using HarmonyLib;
using Kingmaker.Code.UI.MVVM.VM.ActionBar;
using Kingmaker.Networking;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(ActionBarSlotVM), nameof(ActionBarSlotVM.HandleRoleSet), typeof(string))]
    internal static class ActionBarSlotVM_HandleRoleSet_NoSpam_Patch
    {
        private static bool s_loggedActive;

        private static bool Prefix(ActionBarSlotVM __instance, string entityId)
        {
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return true;                                     // solo: vanilla (events never fire anyway)
                var slot = __instance.MechanicActionBarSlot;
                var unit = slot != null ? slot.Unit : null;
                if (unit == null)
                    return false;                                    // unitless slot: nothing to refresh, and
                                                                     // refreshing would NRE -- skip
                if (unit.UniqueId != entityId)
                    return false;                                    // event is about another entity: this
                                                                     // slot's state cannot have changed
                if (!s_loggedActive)
                {
                    s_loggedActive = true;
                    MultiplayerStabilityMain.Log("[ActionBarFix] Active -- action-bar slots refresh only on their own unit's role events.");
                }
                return true;                                         // our unit's role changed: vanilla refresh
            }
            catch (Exception)
            {
                return true;                                         // fail-open: vanilla behaviour
            }
        }
    }
}

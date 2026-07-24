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
// global exception suppressor -- real exceptions elsewhere keep their full context.
// UNGATED (no IsMultiplayer check): the filtering invariant is valid in EVERY context, including teardown
// and the rare non-departure raisers (the net_allow_one cheat raises the room callbacks; PlayerRole.ForceSet
// raises HandleRoleSet) -- an unrelated entity's role event cannot affect a slot, and a unitless slot has no
// valid refresh work. No instantaneous player-count test is required or wanted (the v0.8.16 gate disabled
// the guard during 2->1 departures, the storm's biggest window). Fail-open.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Code.UI.MVVM.VM.ActionBar;

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
                // Deliberately NO IsMultiplayer gate (v0.8.17): the filtering invariant below is valid in
                // every context -- an unrelated entity's role event cannot change this slot's state, no
                // matter who raised it (departure teardown, PlayerRole.ForceSet, cheats). The instantaneous
                // PlayerCount>1 check actively DISABLED the guard during 2->1 departures (Owlcat removes
                // the departing player BEFORE raising the callbacks) -- the captured storm's biggest window
                // (~270 of 425 stacks in the field capture).
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

    // Second half (v0.8.16, from the 0.8.14 three-player capture: 425 residual exception stacks during
    // room departure/re-entry): HandlePlayerEnteredRoom and HandlePlayerLeftRoom do the SAME unconditional
    // slot refresh and NRE the same way on unitless slots. These are per-player events (once per join/
    // leave, not per-entity -- hence the smaller storm), and a player change legitimately affects every
    // slot's net-role availability, so no entity filter applies here -- only the unitless skip.
    // Targets resolved by explicit name + single-Player-parameter match (the 0.8.13 lesson: never
    // name-only lookups) without needing a compile-time Photon.Realtime reference.
    [HarmonyPatch]
    internal static class ActionBarSlotVM_RoomEvents_NoSpam_Patch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in new[] { "HandlePlayerEnteredRoom", "HandlePlayerLeftRoom" })
            {
                MethodBase found = null;
                foreach (var m in AccessTools.GetDeclaredMethods(typeof(ActionBarSlotVM)))
                {
                    if (m.Name == name && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType.Name == "Player")
                    {
                        found = m;
                        break;
                    }
                }
                if (found != null)
                    yield return found;
                else
                    MultiplayerStabilityMain.Log("[ActionBarFix][ERR] ActionBarSlotVM." + name + "(Player) not found -- room-event path unguarded.");
            }
        }

        private static bool Prefix(ActionBarSlotVM __instance)
        {
            try
            {
                // No IsMultiplayer gate -- same reasoning as the role-event guard above: skipping a
                // unitless slot's refresh is valid in every context (there is no valid work to do), and
                // the instantaneous gate opted out exactly during 2->1 departures.
                var slot = __instance.MechanicActionBarSlot;
                if (slot == null || slot.Unit == null)
                    return false;                                    // unitless slot: refresh would NRE -- skip
                return true;                                         // real slot: vanilla refresh
            }
            catch (Exception)
            {
                return true;                                         // fail-open: vanilla behaviour
            }
        }
    }
}

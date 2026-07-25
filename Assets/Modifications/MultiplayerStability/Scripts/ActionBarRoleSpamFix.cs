// Action-bar role-event filtering for the 600 MB log incident (2026-07-11; chain verified in
// the decompile, counts from retained captures: 18,036 identical exception stacks in the 0.6.9 client log).
//
// When a co-op player leaves, PlayerRole.OnPlayerLeftRoom (PlayerRole.cs:112) raises
// INetRoleSetHandler.HandleRoleSet(entityId) for every controlled entity whose role group contained the
// leaver (~1,500 on a live save). ActionBarSlotVM.HandleRoleSet (ActionBarSlotVM.cs:560) does not filter
// by entityId and unconditionally refreshes its slot, so all 12 action-bar slots refresh ~1,500
// times each, and any slot whose MechanicActionBarSlot has a missing Unit throws NullReferenceException
// (MechanicActionBarSlot.IsDisabled/IsPossibleActive dereference this.Unit) on each refresh.
// 1,500 x 12 full exception stacks in seconds; GameLogFull has no size cap (LogSinkFactory.cs:29 passes
// int.MaxValue), so the log grows unbounded. The incident produced 600 MB files and delayed the
// disconnect/recovery path.
//
// Fix (UI-only, no simulation contact): prefix on ActionBarSlotVM.HandleRoleSet:
//   1. unitless slot -> skip the refresh entirely (there is nothing to update and the refresh would throw);
//   2. event about a different entity than this slot's unit -> skip (removes the ~1,500x amplification;
//      the slot still refreshes when its unit's role changes).
// The matching event still runs the original handler. This is not a global exception suppressor.
// Ungated (no IsMultiplayer check): the filtering invariant is valid in every context, including teardown
// and the rare non-departure raisers (the net_allow_one cheat raises the room callbacks; PlayerRole.ForceSet
// raises HandleRoleSet) -- an unrelated entity's role event cannot affect a slot, and a unitless slot has no
// valid refresh work. An instantaneous player-count test is incorrect here because Owlcat removes the
// departing player before the 2->1 departure callbacks. Fail-open.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Code.UI.MVVM.VM.ActionBar;
using Kingmaker.EntitySystem.Entities;
using PhotonPlayer = Photon.Realtime.Player;

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
                // No IsMultiplayer gate (v0.8.17): the filtering invariant below is valid in
                // every context -- an unrelated entity's role event cannot change this slot's state, no
                // matter who raised it (departure teardown, PlayerRole.ForceSet, cheats). The instantaneous
                // PlayerCount>1 check disabled the guard during 2->1 departures (Owlcat removes
                // the departing player before raising the callbacks), which was the highest-volume window
                // (~270 of 425 stacks in the field capture).
                var slot = __instance.MechanicActionBarSlot;
                var unit = slot != null ? slot.Unit : null;
                if (unit == null)
                    return false;                                    // unitless slot: nothing to refresh, and
                                                                     // refreshing would NRE -- skip
                if (RoleEntityId(unit) != entityId)
                    return false;                                    // event is about another entity: this
                                                                     // slot's state cannot have changed
                if (!s_loggedActive)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[ActionBarFix] Active; slots refresh only for their normalized role owner.");
                    s_loggedActive = true;
                }
                return true;                                         // our unit's role changed: vanilla refresh
            }
            catch (Exception)
            {
                return true;                                         // fail-open: vanilla behaviour
            }
        }

        // Match PlayerRoleExtenstion.Can exactly. Starships use the main character's role key, and pets
        // use their master's key; comparing every slot to unit.UniqueId wrongly discarded valid updates.
        private static string RoleEntityId(BaseUnitEntity unit)
        {
            if (unit is StarshipEntity)
                return Game.Instance.Player.MainCharacter.Id;
            if (unit.IsPet && unit.Master != null)
                return unit.Master.UniqueId;
            return unit.UniqueId;
        }
    }

    // Second half (v0.8.16, from the 0.8.14 three-player capture: 425 residual exception stacks during
    // room departure/re-entry): HandlePlayerEnteredRoom and HandlePlayerLeftRoom do the SAME unconditional
    // slot refresh and NRE the same way on unitless slots. These are per-player events (once per join/
    // leave, not per-entity -- hence the smaller storm), and a player change legitimately affects every
    // slot's net-role availability, so no entity filter applies here -- only the unitless skip.
    // Resolve targets by explicit name and a single Player parameter, avoiding ambiguous
    // name-only lookups without adding a compile-time Photon.Realtime reference.
    [HarmonyPatch]
    internal static class ActionBarSlotVM_RoomEvents_NoSpam_Patch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var targets = new List<MethodBase>();
            foreach (var name in new[] { "HandlePlayerEnteredRoom", "HandlePlayerLeftRoom" })
            {
                MethodBase found = AccessTools.Method(
                    typeof(ActionBarSlotVM), name, new[] { typeof(PhotonPlayer) });
                if (found == null)
                    throw new MissingMethodException(
                        "ActionBarSlotVM." + name + "(Player) not found; room-event class inactive.");
                targets.Add(found);
            }
            return targets;
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

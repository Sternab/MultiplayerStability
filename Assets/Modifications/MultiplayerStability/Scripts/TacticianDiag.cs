// Tactician momentum diagnostic. TacticalAdvantagePassive accumulates a fractional remainder in
// Data.MomentumThisCombat and awards a hashed buff each time the value crosses 100. Rogue Trader's
// Data.GetHash128 omits the remainder, so different remainders are invisible until only one peer crosses
// the threshold. This diagnostic records the existing value without calling RequestSavableData: observing
// the component must not create or alter simulation state.
//
// Dark Heresy marks TacticalAdvantagePassive obsolete and removes its momentum rulebook handler. That is
// useful evidence that the subsystem changed, but it is not a directly portable correction for this game.
// This class remains diagnostic only, subset-safe, and gated to an active multiplayer session.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.EntitySystem;
using Kingmaker.Networking;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.UnitLogic.FactLogic;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(TacticalAdvantagePassive), "OnEventDidTrigger",
        typeof(RulePerformMomentumChange))]
    internal static class TacticianDiag
    {
        private const int MaxRecordsPerSession = 4000;
        private static readonly FieldInfo ComponentsDataField =
            AccessTools.Field(typeof(EntityFact), "m_ComponentsData");
        private static readonly Dictionary<string, int> Ordinals = new Dictionary<string, int>();
        private static int s_ordinalTick = int.MinValue;
        private static int s_records;
        private static bool s_capLogged;

        private static bool Prepare()
        {
            if (ComponentsDataField != null)
                return true;
            MultiplayerStabilityMain.LogNoThrow(
                "[TacticianDiag][ERR] EntityFact.m_ComponentsData not found; diagnostic inactive.");
            return false;
        }

        private struct EventState
        {
            internal bool Enabled;
            internal int Tick;
            internal int Sequence;
            internal string Owner;
            internal int? Before;
        }

        private static void Prefix(TacticalAdvantagePassive __instance, out EventState __state)
        {
            __state = default(EventState);
            try
            {
                if (!NetworkingManager.IsMultiplayer || s_records >= MaxRecordsPerSession)
                {
                    LogCapOnce();
                    return;
                }

                int tick = Game.Instance.RealTimeController.CurrentNetworkTick;
                string owner = OwnerId(__instance);
                if (tick != s_ordinalTick)
                {
                    s_ordinalTick = tick;
                    Ordinals.Clear();
                }

                int sequence;
                Ordinals.TryGetValue(owner, out sequence);
                Ordinals[owner] = sequence + 1;
                __state = new EventState
                {
                    Enabled = true,
                    Tick = tick,
                    Sequence = sequence,
                    Owner = owner,
                    Before = ReadExistingRemainder(__instance)
                };
            }
            catch (Exception)
            {
                // Diagnostic only.
            }
        }

        private static void Postfix(
            TacticalAdvantagePassive __instance,
            RulePerformMomentumChange evt,
            EventState __state)
        {
            if (!__state.Enabled)
                return;
            try
            {
                int? after = ReadExistingRemainder(__instance);
                s_records++;
                MultiplayerStabilityMain.LogNoThrow(
                    "[TacticianDiag] momentum tick=" + __state.Tick
                    + " owner=" + __state.Owner
                    + " seq=" + __state.Sequence
                    + " delta=" + evt.ResultDeltaValue
                    + " remainder=" + Format(__state.Before) + "->" + Format(after));
            }
            catch (Exception)
            {
                // Diagnostic only.
            }
        }

        internal static int? ReadExistingRemainder(TacticalAdvantagePassive component)
        {
            try
            {
                var runtime = component.Runtime;
                var fact = runtime?.Fact;
                string componentName = runtime?.SourceBlueprintComponentName;
                var dictionary = ComponentsDataField?.GetValue(fact) as IDictionary;
                if (dictionary == null || string.IsNullOrEmpty(componentName)
                    || !dictionary.Contains(componentName))
                    return null;

                var values = dictionary[componentName] as IEnumerable;
                if (values == null)
                    return null;
                foreach (object value in values)
                {
                    var data = value as TacticalAdvantagePassive.Data;
                    if (data != null)
                        return data.MomentumThisCombat;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        internal static string OwnerId(TacticalAdvantagePassive component)
        {
            try
            {
                object owner = component.Runtime?.Owner;
                object id = owner != null
                    ? AccessTools.Property(owner.GetType(), "UniqueId")?.GetValue(owner)
                    : null;
                return id?.ToString() ?? "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        internal static void ResetSession()
        {
            Ordinals.Clear();
            s_ordinalTick = int.MinValue;
            s_records = 0;
            s_capLogged = false;
        }

        private static string Format(int? value) => value.HasValue ? value.Value.ToString() : "missing";

        private static void LogCapOnce()
        {
            if (s_records < MaxRecordsPerSession || s_capLogged)
                return;
            MultiplayerStabilityMain.LogNoThrow(
                "[TacticianDiag] record cap reached; further momentum events are omitted this session.");
            s_capLogged = true;
        }
    }

    [HarmonyPatch(typeof(TacticalAdvantagePassive), nameof(TacticalAdvantagePassive.HandleTurnBasedModeSwitched))]
    internal static class TacticianDiag_Reset_Patch
    {
        private static void Prefix(
            TacticalAdvantagePassive __instance,
            bool isTurnBased,
            out int? __state)
        {
            __state = null;
            if (!NetworkingManager.IsMultiplayer || isTurnBased)
                return;
            __state = TacticianDiag.ReadExistingRemainder(__instance);
        }

        private static void Postfix(
            TacticalAdvantagePassive __instance,
            bool isTurnBased,
            int? __state)
        {
            if (!NetworkingManager.IsMultiplayer || isTurnBased)
                return;
            int tick;
            try
            {
                tick = Game.Instance.RealTimeController.CurrentNetworkTick;
            }
            catch (Exception)
            {
                tick = -1;
            }
            MultiplayerStabilityMain.LogNoThrow(
                "[TacticianDiag] reset tick=" + tick
                + " owner=" + TacticianDiag.OwnerId(__instance)
                + " remainder=" + (__state.HasValue ? __state.Value.ToString() : "missing")
                + "->" + (TacticianDiag.ReadExistingRemainder(__instance)?.ToString() ?? "missing"));
        }
    }

    [HarmonyPatch(typeof(ModsNetManager), nameof(ModsNetManager.OnLeave))]
    internal static class ModsNetManager_OnLeave_TacticianDiagReset_Patch
    {
        private static void Postfix() => TacticianDiag.ResetSession();
    }
}

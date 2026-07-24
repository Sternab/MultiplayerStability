// Tactician momentum DIAGNOSTIC -- log-only: TacticalAdvantagePassive accumulates
// a fractional remainder in data.MomentumThisCombat (+= evt.ResultDeltaValue * multiplier; -= 100 per
// crossing) and adds TacticianTacticalAdvantageBuff on each 100-crossing -- but Data.GetHash128 OMITS the
// accumulator entirely, so a remainder divergence is invisible to desync detection until one peer crosses
// 100 first and mints a one-sided hashed buff (captured @7816039: one peer alone created the buff).
// WHAT ORIGINALLY SPLIT THE REMAINDERS IS UNPROVEN -- possibly an upstream event-delta difference, possibly
// an amplified earlier fork (the charge class fired nearby) -- so per instrument-before-fix this logs every
// momentum event with its delta and post-event remainder; the two-sided diff shows WHERE the remainders
// first part and by how much. Same base-only-hash mistake exists in MomentumReachedTrigger, HunterDodge,
// ChangeVeilDamage (bounded hash audit queued separately). NOT a fix; do not change gameplay or hashing yet.
// Log-only -> subset-safe; MP-gated. The Data object is read reflectively (component runtime data).
using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Networking;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.UnitLogic.FactLogic;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(TacticalAdvantagePassive), "OnEventDidTrigger", typeof(RulePerformMomentumChange))]
    internal static class TacticianDiag
    {
        private static void Postfix(TacticalAdvantagePassive __instance, RulePerformMomentumChange evt)
        {
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return;
                int remainder = int.MinValue;
                try
                {
                    // Component runtime data: resolve the Data instance reflectively and read the
                    // hash-omitted accumulator.
                    object data = null;
                    var dataProp = AccessTools.Property(typeof(TacticalAdvantagePassive), "Data")
                        ?? AccessTools.Property(typeof(TacticalAdvantagePassive).BaseType, "Data");
                    if (dataProp != null)
                        data = dataProp.GetValue(__instance);
                    if (data == null)
                    {
                        var m = AccessTools.Method(typeof(TacticalAdvantagePassive), "RequestSavableData");
                        if (m != null && m.IsGenericMethodDefinition == false)
                            data = m.Invoke(__instance, null);
                    }
                    if (data != null)
                    {
                        var f = AccessTools.Field(data.GetType(), "MomentumThisCombat");
                        if (f != null)
                            remainder = (int)f.GetValue(data);
                    }
                }
                catch (Exception) { }
                object delta = null;
                try
                {
                    delta = AccessTools.Property(typeof(RulePerformMomentumChange), "ResultDeltaValue")?.GetValue(evt)
                        ?? AccessTools.Field(typeof(RulePerformMomentumChange), "ResultDeltaValue")?.GetValue(evt);
                }
                catch (Exception) { }
                MultiplayerStabilityMain.Log("[TacticianDiag] momentum event owner="
                    + OwnerId(__instance)
                    + " tick=" + Game.Instance.RealTimeController.CurrentNetworkTick
                    + " delta=" + (delta ?? "?")
                    + " remainderAfter=" + (remainder == int.MinValue ? "?" : remainder.ToString()));
            }
            catch (Exception)
            {
                // log-only diagnostic: never interfere
            }
        }

        private static string OwnerId(TacticalAdvantagePassive component)
        {
            try
            {
                var owner = AccessTools.Property(typeof(TacticalAdvantagePassive), "Owner")
                    ?? AccessTools.Property(typeof(TacticalAdvantagePassive).BaseType, "Owner");
                var o = owner != null ? owner.GetValue(component) : null;
                var id = o != null ? AccessTools.Property(o.GetType(), "UniqueId")?.GetValue(o) : null;
                return id != null ? id.ToString() : "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}

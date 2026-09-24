// Cyber Eagle Proving Ground extra-turn fanout fix.
//
// The Proving Ground feature distributes EaglePet_ProvingGround_Ally_Marker_Buff to eligible
// allies at combat start. Its ExtraTurnWithReasonTrigger is configured for any unit's interrupt
// turn and applies EaglePet_ProvingGround_Ally_Bonus_Buff to the granted-turn unit. That final buff
// uses replace stacking, so repeated marker callbacks do not add a stronger effect. They do remove
// and recreate the same fact, consume GlobalUuid values, and make the surviving fact context depend
// on subscriber membership and order.
//
// A paired three-player v0.9.7 capture recorded the host applying the bonus 16 times while both
// clients applied it 15 times for the same Officer-granted Abelard turn. The host consumed 11 extra
// GlobalUuid draws and the first entity hashes diverged immediately afterward. All peers spent the
// same momentum and retained the same Tactician remainder, which excludes the Tactician fix from
// that fork. The earlier all-unit ordering patch cannot repair a different marker count.
//
// Fix: for this exact marker blueprint in exact-parity multiplayer, only the marker owned by the
// unit receiving the interrupt turn may run. The feature already places a marker on each eligible
// ally, so this preserves the single resulting bonus while removing redundant replacement writes.
// Detached, disposed, or otherwise stale marker runtimes are also rejected. Other
// ExtraTurnWithReasonTrigger blueprints, solo play, and incompatible sessions remain vanilla.
// Harmony target: ExtraTurnWithReasonTrigger.HandleOnInterruptCurrentTurn. Runtime lookup or
// validation failures log and use vanilla behavior; the patch only suppresses a callback after it
// has positively identified this marker and validated the event owner.
//
// Dark Heresy separates extra-turn owner restrictions from explicit source-and-target restrictions.
// That supports the diagnosis that Rogue Trader's older trigger contract is ambiguous, but this is
// a content-specific Rogue Trader fix rather than a backport of the newer component.
using System;
using HarmonyLib;
using Kingmaker.Designers.Mechanics.Facts;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.UnitLogic.Buffs;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(ExtraTurnWithReasonTrigger),
        nameof(ExtraTurnWithReasonTrigger.HandleOnInterruptCurrentTurn))]
    internal static class ProvingGroundExtraTurnFix
    {
        private const string MarkerBlueprintGuid = "b742a077d9d246c6ab4a3ae4a99d1372";
        private const int MaxStaleLogs = 20;

        private static bool s_loggedActive;
        private static int s_staleLogs;

        private static bool Prefix(ExtraTurnWithReasonTrigger __instance)
        {
            if (!MultiplayerCompatibility.SimulationFixesEnabled)
                return true;

            EntityFactComponent runtime;
            EntityFact fact;
            bool isProvingGroundMarker;
            try
            {
                runtime = __instance?.Runtime;
                fact = runtime?.Fact;
                isProvingGroundMarker = fact?.Blueprint != null
                    && string.Equals(fact.Blueprint.AssetGuid, MarkerBlueprintGuid,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[ProvingGroundFix][ERR] Marker runtime unavailable; using vanilla callback: "
                    + e.Message);
                return true;
            }

            if (!isProvingGroundMarker)
                return true;

            BaseUnitEntity owner;
            BaseUnitEntity turnOwner;
            bool currentMarker;
            try
            {
                owner = runtime.Owner as BaseUnitEntity;
                turnOwner = EventInvokerExtensions.MechanicEntity as BaseUnitEntity;
                currentMarker = IsCurrentMarker(runtime, fact, owner);
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[ProvingGroundFix][ERR] Could not validate the marker; using vanilla callback: "
                    + e.Message);
                return true;
            }

            if (turnOwner == null)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[ProvingGroundFix][ERR] Interrupt-turn owner is not a unit; using vanilla callback.");
                return true;
            }

            LogActiveOnce();

            if (!currentMarker)
            {
                LogStaleSkip(owner);
                return false;
            }

            return ReferenceEquals(owner, turnOwner);
        }

        private static bool IsCurrentMarker(
            EntityFactComponent runtime,
            EntityFact fact,
            BaseUnitEntity owner)
        {
            var buff = fact as Buff;
            return runtime != null
                && !runtime.IsDisposed
                && runtime.IsActive
                && runtime.IsSubscribedOnEventBus
                && !fact.IsDisposed
                && fact.IsAttached
                && fact.IsActive
                && fact.IsSubscribedOnEventBus
                && owner != null
                && !owner.IsDisposed
                && !owner.IsDisposingNow
                && owner.IsInGame
                && buff != null
                && ReferenceEquals(fact.Owner, owner)
                && owner.Buffs != null
                && owner.Buffs.RawFacts != null
                && owner.Buffs.RawFacts.Contains(buff);
        }

        private static void LogActiveOnce()
        {
            if (s_loggedActive)
                return;

            try
            {
                MultiplayerStabilityMain.Log(
                    "[ProvingGroundFix] Active; only the granted unit's current marker may apply the bonus.");
                s_loggedActive = true;
            }
            catch
            {
                // Retry the presence signal on the next Proving Ground callback.
            }
        }

        private static void LogStaleSkip(BaseUnitEntity owner)
        {
            if (s_staleLogs >= MaxStaleLogs)
                return;

            s_staleLogs++;
            MultiplayerStabilityMain.LogNoThrow(
                "[ProvingGroundFix] Skipped stale marker callback owner="
                + (owner?.UniqueId ?? "null") + ".");
        }
    }
}

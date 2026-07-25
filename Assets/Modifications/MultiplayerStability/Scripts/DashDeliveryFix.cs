// Dash delivery fix -- dash-through abilities must not gate their effects on view-layer movement.
//
// AbilityCustomDirectMovement (Macabre Dance, and any other dash-through ability built on the component)
// precomputes its target set deterministically from the grid pattern along the dash path
// (GetAllTargetUnits), but DELIVERS per poll while the view-layer movement agent is moving
// (Deliver :288 `while movementAgent.IsReallyMoving`), gating each target on the caster's live mid-dash
// position (HandleNecessaryTargets :361-363). The view agent advances per rendered frame, so the sampled
// positions differ across machines. A target detected on one peer can be missed permanently on another
// because no catch-up pass covers mid-path targets. Capture 13 (2026-07-05, both v0.6.6) recorded this:
// Macabre Dance applied EnemyEffect on a Necron on one machine and CoverEffect only on the other ->
// permanent GlobalUuid count fork. Same hazard shape as ProjectileRngFix, for dashes.
//
// Fix: in multiplayer, mid-dash delivery is deferred while the synchronized
// BaseUnitEntity.State.IsCharging flag is true. Deliver clears that flag only after writing the caster's
// mechanics position to the dash endpoint (:313-318), so the following unconditional call handles every
// precomputed target from the correct position and cannot be skipped by a local movement-agent state.
// range-checked main actions (Charge's melee attack!) resolve from the correct position. v1 of this fix
// delivered everything on the FIRST poll instead and broke Charge: the attack ran while the caster was
// still at the dash start, out of melee reach. Handling order is sorted by entity UniqueId (ordinal):
// identical on all clients (same entities carry same ids -- the invariant lockstep maintains), unlike the
// `targets` array order, which comes from a HashSet<CustomGridNodeBase> enumeration (reference-hashed =
// memory order = client-local). Residues accepted: effects land at dash END rather than mid-pass (visual
// timing only), and the delivery tick can still skew by the dash duration (count-equal streams re-align,
// but tick-skewed side effects are not guaranteed harmless: threshold-crossing accumulators like the
// Tactician momentum remainder can latch a skew permanently). This is therefore a scoped mitigation for
// candidate delivery and charge completion, not proof that every dash side effect is tick-identical.
// Solo/unresolved/mixed sessions use vanilla.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Mechanics.Entities;
using Kingmaker.Networking;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(AbilityCustomDirectMovement), "HandleNecessaryTargets")]
    internal static class AbilityCustomDirectMovement_HandleNecessaryTargets_Deterministic_Patch
    {
        private delegate void HandleTargetDelegate(
            AbilityCustomDirectMovement self,
            AbilityExecutionContext context,
            MechanicEntity target);
        private static HandleTargetDelegate s_handleTarget;

        private static bool s_loggedActive;
        private static bool s_loggedError;
        private static readonly MechanicEntity[] s_empty = new MechanicEntity[0];

        private static bool Prepare()
        {
            try
            {
                MethodInfo method = AccessTools.Method(
                    typeof(AbilityCustomDirectMovement),
                    "HandleTarget",
                    new[] { typeof(AbilityExecutionContext), typeof(MechanicEntity) });
                if (method == null)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[DashFix][ERR] HandleTarget signature not found; patch inactive.");
                    return false;
                }
                s_handleTarget = AccessTools.MethodDelegate<HandleTargetDelegate>(method);
                return s_handleTarget != null;
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[DashFix][ERR] HandleTarget binding failed; patch inactive: " + e.Message);
                return false;
            }
        }

        private static bool Prefix(AbilityCustomDirectMovement __instance, AbilityExecutionContext context,
            MechanicEntity[] targets, HashSet<MechanicEntity> handledTargets, ref IEnumerable<MechanicEntity> __result)
        {
            try
            {
                if (!MultiplayerCompatibility.SimulationFixesEnabled)
                    return true;
                // The final call is after Deliver writes the endpoint and clears this hashed flag.
                // This identifies the endpoint phase; it does not make the locally-driven completion tick equal.
                var casterUnit = context.Caster as BaseUnitEntity;
                if (casterUnit == null)
                    return true;
                if (casterUnit.State.IsCharging)
                {
                    __result = s_empty;
                    return false;
                }
                var pending = new List<MechanicEntity>();
                foreach (var target in targets)
                {
                    if (!handledTargets.Contains(target))
                        pending.Add(target);
                }
                pending.Sort((a, b) => string.CompareOrdinal(a.UniqueId, b.UniqueId));
                foreach (var target in pending)
                {
                    try
                    {
                        s_handleTarget(__instance, context, target);
                    }
                    catch (Exception e)
                    {
                        MultiplayerStabilityMain.LogNoThrow(
                            "[DashFix][ERR] target actions failed for " + target + ": " + e.Message);
                    }
                    handledTargets.Add(target);
                }
                __result = pending;
                if (!s_loggedActive && pending.Count > 0)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[DashFix] Active; " + pending.Count
                        + " deferred target(s) handled after synchronized charge completion.");
                    s_loggedActive = true;
                }
                return false;
            }
            catch (Exception e)
            {
                if (!s_loggedError)
                {
                    s_loggedError = true;
                    MultiplayerStabilityMain.LogNoThrow(
                        "[DashFix][ERR] falling back to vanilla delivery: " + e);
                }
                return true;
            }
        }
    }
}

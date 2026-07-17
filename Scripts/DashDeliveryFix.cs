// Dash delivery fix -- dash-through abilities must not gate their effects on view-layer movement.
//
// AbilityCustomDirectMovement (Macabre Dance, and any other dash-through ability built on the component)
// precomputes its target set deterministically from the grid pattern along the dash path
// (GetAllTargetUnits), but DELIVERS per poll while the view-layer movement agent is moving
// (Deliver :288 `while movementAgent.IsReallyMoving`), gating each target on the caster's LIVE mid-dash
// position (HandleNecessaryTargets :361-363). The view agent advances per rendered frame, so the sampled
// positions differ across machines -- a target one client catches in passing can be MISSED FOREVER on the
// other (no catch-up pass covers mid-path targets). Field-proven by capture 13 (2026-07-05, both v0.6.6):
// Macabre Dance applied EnemyEffect on a Necron on one machine and CoverEffect only on the other ->
// permanent GlobalUuid count fork. Same hazard shape as ProjectileRngFix, for dashes.
//
// Fix: in multiplayer, mid-dash delivery is DEFERRED (in-loop calls, recognizable by the movement agent
// still moving, handle nothing) and the unconditional post-movement call handles EVERY precomputed target
// at once -- Deliver sets the caster's mechanics position to the dash endpoint (:313) BEFORE that call, so
// range-checked main actions (Charge's melee attack!) resolve from the correct position. v1 of this fix
// delivered everything on the FIRST poll instead and broke Charge: the attack ran while the caster was
// still at the dash start, out of melee reach. Handling order is sorted by entity UniqueId (ordinal):
// identical on all clients (same entities carry same ids -- the invariant lockstep maintains), unlike the
// `targets` array order, which comes from a HashSet<CustomGridNodeBase> enumeration (reference-hashed =
// memory order = client-local). Residues accepted: effects land at dash END rather than mid-pass (visual
// timing only), and the delivery tick can still skew by the dash duration (count-equal, self-heals). On
// the engine's own 5s force-finish error path the agent may still report moving at the final call -- then
// delivery is skipped symmetrically (a fizzle on an already-errored cast, not a fork).
// Solo behaviour untouched. Both machines must run the mod (the standing install rule).
using System;
using System.Collections.Generic;
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
        private delegate void HandleTargetDelegate(AbilityCustomDirectMovement self, AbilityExecutionContext context, MechanicEntity target);
        private static readonly HandleTargetDelegate s_handleTarget = AccessTools.MethodDelegate<HandleTargetDelegate>(
            AccessTools.Method(typeof(AbilityCustomDirectMovement), "HandleTarget"));

        private static bool s_loggedActive;
        private static bool s_loggedError;
        private static readonly MechanicEntity[] s_empty = new MechanicEntity[0];

        private static bool Prefix(AbilityCustomDirectMovement __instance, AbilityExecutionContext context,
            MechanicEntity[] targets, HashSet<MechanicEntity> handledTargets, ref IEnumerable<MechanicEntity> __result)
        {
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return true;
                // Mid-dash (view agent still moving): defer -- the post-movement call delivers everything
                // from the final position, where range-checked actions resolve correctly.
                var casterUnit = context.Caster as AbstractUnitEntity;
                var agent = casterUnit != null ? casterUnit.MaybeMovementAgent : null;
                if (agent != null && agent.IsReallyMoving)
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
                        MultiplayerStabilityMain.Log("[DashFix][ERR] target actions failed for " + target + ": " + e.Message);
                    }
                    handledTargets.Add(target);
                }
                __result = pending;
                if (!s_loggedActive && pending.Count > 0)
                {
                    s_loggedActive = true;
                    MultiplayerStabilityMain.Log("[DashFix] Active -- dash-through delivery deterministic ("
                        + pending.Count + " target(s) handled at once).");
                }
                return false;
            }
            catch (Exception e)
            {
                if (!s_loggedError)
                {
                    s_loggedError = true;
                    MultiplayerStabilityMain.Log("[DashFix][ERR] falling back to vanilla delivery: " + e);
                }
                return true;
            }
        }
    }
}

// Projectile position fix. ProjectileRngFix handles stream advancement; this component handles geometry.
//
// View state, including camera, fog, renderers, animation bones, and View availability, may differ
// between peers. Mechanical positions therefore need to come from
// entity/grid state or deterministic offsets. Solasta carries visual and deterministic positions separately
// (WorldLocationCharacter reconstructs bones from deterministic position; miss/travel math never reads live
// transforms). Rogue Trader instead feeds live view-bone transforms into mechanics in three places:
//
//  1. Projectile.GetTargetPoint (:328): if a target locator bone was stored at launch (only possible when
//     the target's View had a ParticlesSnapMap), returns the bone's live
//     transform position. That point feeds ricochet leg selection and grenade push direction
//     (ContextActionPush :72). ProjectileRngFix aligned the RNG streams but a client with the SnapMap and a
//     client without still compute different mechanical geometry with identical RNG state.
//  2. Projectile.GetTargetPointForStarship (:347, the long-open sibling): draws a hashed Projectiles-stream
//     hull point only when a StarshipView exists, and then multiplies live transforms.
//  3. AbilityProjectileAttackLineHelper.TryGetTargetPointByRandomLocator (:248): ParticlesSnapMap-gated
//     hashed draw (PFStatefulRandom.UnitLogic.Abilities) over torso locators, then the live bone position
//     steers the attack line.
//
// Fix: in multiplayer, every site takes its existing no-view fallback:
//   GetTargetPoint -> Target.Point + m_MisdirectionOffset (vanilla :343);
//   starship        -> Position + Vector3.up               (vanilla :352, the null-StarshipView branch);
//   random locator  -> return false                        (callers use the deterministic node+1m path).
// This also covers the starship conditional draw. In multiplayer, projectiles aim at the target's base
// point instead of a random bone, matching vanilla behavior for a SnapMap-less target. Solo is unchanged;
// all prefixes fail open on exception.
using System;
using HarmonyLib;
using Kingmaker.Controllers.Projectiles;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Networking;
using Kingmaker.UnitLogic.Abilities.Components.ProjectileAttack;
using UnityEngine;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.GetTargetPoint))]
    internal static class Projectile_GetTargetPoint_Deterministic_Patch
    {
        private static readonly AccessTools.FieldRef<Projectile, Vector3> s_targetPosition =
            AccessTools.FieldRefAccess<Projectile, Vector3>("m_TargetPosition");
        private static readonly AccessTools.FieldRef<Projectile, Vector3> s_misdirection =
            AccessTools.FieldRefAccess<Projectile, Vector3>("m_MisdirectionOffset");

        private static bool s_loggedActive;

        private static bool Prefix(Projectile __instance, ref Vector3 __result)
        {
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return true;                                   // solo: vanilla (locator/hull-point visuals)
                var starship = __instance.Target.Entity as StarshipEntity;
                if (starship != null)
                {
                    // Vanilla's own null-StarshipView fallback -- deterministic, and skips the
                    // view-conditional hashed hull-point draw entirely.
                    __result = starship.Position + Vector3.up;
                }
                else if (__instance.Blueprint.FreezeEndPosition && s_targetPosition(__instance) != Vector3.zero)
                {
                    __result = s_targetPosition(__instance);       // frozen value (itself deterministic in MP
                                                                   // -- it is captured from this method)
                }
                else
                {
                    // Vanilla's own no-locator fallback: entity/grid point + the (mechanics-side) miss offset.
                    __result = __instance.Target.Point + s_misdirection(__instance);
                }
                if (!s_loggedActive)
                {
                    s_loggedActive = true;
                    MultiplayerStabilityMain.Log("[ProjectilePos] Active -- projectile target geometry is entity-derived in multiplayer (no live view bones).");
                }
                return false;
            }
            catch (Exception)
            {
                return true;                                       // fail-open: vanilla path
            }
        }
    }

    // View-gated hashed draw + live-bone geometry for the attack line. In MP, report "no locator" -- the
    // callers' own fallback (grid node position + 1m) is deterministic, and the conditional
    // PFStatefulRandom.UnitLogic.Abilities draw never happens on either machine.
    [HarmonyPatch(typeof(AbilityProjectileAttackLineHelper), "TryGetTargetPointByRandomLocator")]
    internal static class AttackLine_RandomLocator_Deterministic_Patch
    {
        private static bool Prefix(ref bool __result, ref Vector3 result)
        {
            if (!NetworkingManager.IsMultiplayer)
                return true;
            result = default(Vector3);
            __result = false;
            return false;
        }
    }
}

// Burst-fire and projectile RNG fix (confirmed by the Argenta heavy-bolter reproduction).
//
// Projectile.BeforeLaunch picks a random visual aim bone via LinqExtensions.Random<FxBone>(locators,
// PFStatefulRandom.Controllers.Projectiles) -- a draw from a hashed stream that only happens when
// the target unit's View has a ParticlesSnapMap (Projectile.cs:440-450). View presence is client-local
// (culling/LOD/sleep). When it differs between co-op clients, one client draws an extra number, the stream
// offsets diverge, and every later projectile Speed draw (Projectile.cs:408 -- which sets the tick hit
// rules fire on) differs between clients: damage and kills resolve on different ticks. Burst weapons
// launch many projectiles per attack, increasing the likelihood of this divergence.
//
// Fix: retarget that call to a same-shaped helper that deterministically takes the first locator and
// never draws -- so the hashed stream advances identically (zero times) on both clients regardless of view
// state. Deterministic rather than client-local random because the chosen bone's position feeds mechanics:
// ricochet launch point/range (AbilityProjectileAttackLineHelper.cs:117 -> RuleCalculateOverpenetration)
// and grenade push direction (ContextActionPush.cs:72). The engine itself has a first-locator precedent
// (SnapMapBase.GetLocatorFirst). The Speed draw in the same method stays on the hashed stream -- both
// clients run it identically inside the simulation.
//
// ProjectilePositionFix.cs (v0.8.7) handles GetTargetPointForStarship's conditional draw,
// TryGetTargetPointByRandomLocator's conditional draw, and the residual live-bone geometry this fix left
// behind (the chosen locator's live Transform still fed GetTargetPoint -> ricochet/push mechanics; a client
// with a ParticlesSnapMap and one without computed different geometry despite identical RNG -- Solasta
// source-comparison finding). In MP all three now take the engine's own deterministic no-view fallbacks.
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Kingmaker.Controllers.Projectiles;
using Kingmaker.Utility.DotNetExtensions;
using Kingmaker.Utility.StatefulRandom;
using Kingmaker.Visual.Particles;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.BeforeLaunch))]
    internal static class Projectile_BeforeLaunch_DeterministicFxBone_Patch
    {
        public static FxBone FirstLocator(IReadOnlyList<FxBone> locators, StatefulRandom unused)
        {
            return (locators != null && locators.Count > 0) ? locators[0] : null;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var replacement = AccessTools.Method(
                typeof(Projectile_BeforeLaunch_DeterministicFxBone_Patch), nameof(FirstLocator));
            bool patched = false;
            foreach (var ins in instructions)
            {
                if (!patched && ins.opcode == OpCodes.Call && ins.operand is MethodInfo mi
                    && mi.IsGenericMethod && mi.Name == "Random"
                    && mi.DeclaringType == typeof(LinqExtensions)
                    && mi.GetGenericArguments()[0] == typeof(FxBone))
                {
                    ins.operand = replacement;   // same opcode + stack shape; labels/blocks preserved
                    patched = true;
                }
                yield return ins;
            }
            if (patched)
                MultiplayerStabilityMain.Log("[ProjectileFix] BeforeLaunch FxBone pick made deterministic (hashed Projectiles stream protected).");
            else
                MultiplayerStabilityMain.Log("[ProjectileFix][ERR] Random<FxBone> call not found in BeforeLaunch -- fix inactive (game update changed the method?).");
        }
    }
}

// Burst-fire and projectile RNG fix (confirmed by the Argenta heavy-bolter reproduction).
//
// Projectile.BeforeLaunch picks a random visual aim bone via LinqExtensions.Random<FxBone>(locators,
// PFStatefulRandom.Controllers.Projectiles) -- a draw from a hashed stream that only happens when
// the target unit's View has a ParticlesSnapMap (Projectile.cs:440-450). View presence is client-local
// (culling/LOD/sleep). When it differs between co-op clients, one client draws an extra number, the stream
// offsets diverge, and later projectile Speed draws (Projectile.cs:408 -- which sets the tick hit
// rules fire on) can differ between clients. Damage and kills can then resolve on different ticks.
// Burst weapons launch many projectiles per attack, increasing the exposure to this divergence.
//
// Fix: retarget that call to a same-shaped helper that deterministically takes the first locator and
// never draws -- so the hashed stream advances identically (zero times) on both clients regardless of view
// state. Deterministic rather than client-local random because the chosen bone's position feeds mechanics:
// ricochet launch point/range (AbilityProjectileAttackLineHelper.cs:117 -> RuleCalculateOverpenetration)
// and grenade push direction (ContextActionPush.cs:72). The engine itself has a first-locator precedent
// (SnapMapBase.GetLocatorFirst). The Speed draw in the same method remains on the hashed stream and
// is not changed by this patch.
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
            if (!MultiplayerCompatibility.SimulationFixesEnabled)
                return LinqExtensions.Random<FxBone>(locators, unused);
            return (locators != null && locators.Count > 0) ? locators[0] : null;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var replacement = AccessTools.Method(
                typeof(Projectile_BeforeLaunch_DeterministicFxBone_Patch), nameof(FirstLocator));
            var matches = new List<CodeInstruction>();
            foreach (var ins in code)
            {
                if (ins.opcode == OpCodes.Call && ins.operand is MethodInfo mi
                    && mi.IsGenericMethod && mi.Name == "Random"
                    && mi.DeclaringType == typeof(LinqExtensions)
                    && mi.GetGenericArguments()[0] == typeof(FxBone))
                {
                    matches.Add(ins);
                }
            }
            if (matches.Count != 1 || replacement == null)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[ProjectileFix][ERR] expected one Random<FxBone> call in BeforeLaunch, found "
                    + matches.Count + "; transpiler left method unchanged.");
                return code;
            }

            matches[0].operand = replacement;
            MultiplayerStabilityMain.LogNoThrow(
                "[ProjectileFix] BeforeLaunch FxBone pick gated by exact-build compatibility.");
            return code;
        }
    }
}

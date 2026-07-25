// Mechanical projectile geometry must not depend on live view bones.
//
// Rogue Trader stores a target locator selected from ParticlesSnapMap and may freeze its live Transform
// position into m_TargetPosition. Both locator availability and pose are client-local. The stored value is
// later read by projectile movement, ricochet selection, push direction, and launch raycasts.
//
// Under the exact-build compatibility latch this component uses the engine's existing no-view fallback:
// Target.Point + m_MisdirectionOffset (or starship.Position + Vector3.up). During BeforeLaunch the same
// value drives the raycast, then m_TargetPosition is normalized before the method returns so a
// FreezeEndPosition projectile cannot reintroduce the local locator value on later ticks. Solo,
// unresolved, and mixed builds execute vanilla unchanged.
using System;
using HarmonyLib;
using Kingmaker.Controllers.Projectiles;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities.Components.ProjectileAttack;
using UnityEngine;

namespace MultiplayerStability
{
    internal static class ProjectilePositionAccess
    {
        internal static AccessTools.FieldRef<Projectile, Vector3> TargetPosition;
        internal static AccessTools.FieldRef<Projectile, Vector3> Misdirection;
        private static bool s_prepareAttempted;
        private static bool s_ready;
        [ThreadStatic]
        private static int s_beforeLaunchDepth;

        internal static bool IsBeforeLaunch => s_beforeLaunchDepth > 0;

        internal static bool Prepare()
        {
            if (s_prepareAttempted)
                return s_ready;
            s_prepareAttempted = true;
            try
            {
                TargetPosition =
                    AccessTools.FieldRefAccess<Projectile, Vector3>("m_TargetPosition");
                Misdirection =
                    AccessTools.FieldRefAccess<Projectile, Vector3>("m_MisdirectionOffset");
                s_ready = TargetPosition != null && Misdirection != null;
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[ProjectilePos][ERR] required Projectile fields not found; position patches inactive: "
                    + e.Message);
                s_ready = false;
            }
            return s_ready;
        }

        internal static void EnterBeforeLaunch() => s_beforeLaunchDepth++;

        internal static void ExitBeforeLaunch()
        {
            if (s_beforeLaunchDepth > 0)
                s_beforeLaunchDepth--;
        }

        internal static Vector3 FreshDeterministicTarget(Projectile projectile)
        {
            var starship = projectile.Target.Entity as StarshipEntity;
            if (starship != null)
                return starship.Position + Vector3.up;
            var offset = Misdirection(projectile);
            return projectile.Target.Point + offset;
        }

        internal static Vector3 DeterministicTarget(Projectile projectile)
        {
            if (!IsBeforeLaunch
                && !(projectile.Target.Entity is StarshipEntity)
                && projectile.Blueprint.FreezeEndPosition)
            {
                var frozen = TargetPosition(projectile);
                if (frozen != Vector3.zero)
                    return frozen;
            }
            return FreshDeterministicTarget(projectile);
        }
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.GetTargetPoint))]
    internal static class Projectile_GetTargetPoint_Deterministic_Patch
    {
        private static bool s_loggedActive;

        private static bool Prepare()
        {
            return ProjectilePositionAccess.Prepare();
        }

        private static bool Prefix(Projectile __instance, ref Vector3 __result)
        {
            if (!MultiplayerCompatibility.SimulationFixesEnabled)
                return true;
            try
            {
                __result = ProjectilePositionAccess.DeterministicTarget(__instance);
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[ProjectilePos][ERR] deterministic target failed; using vanilla: " + e.Message);
                return true;
            }

            if (!s_loggedActive)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[ProjectilePos] Active; mechanical target points ignore live view bones.");
                s_loggedActive = true;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.BeforeLaunch))]
    internal static class Projectile_BeforeLaunch_NormalizeFrozenTarget_Patch
    {
        private static bool Prepare()
        {
            return ProjectilePositionAccess.Prepare();
        }

        private static void Prefix(out bool __state)
        {
            __state = MultiplayerCompatibility.SimulationFixesEnabled;
            if (__state)
                ProjectilePositionAccess.EnterBeforeLaunch();
        }

        private static Exception Finalizer(
            Projectile __instance,
            bool __state,
            Exception __exception)
        {
            if (!__state)
                return __exception;
            try
            {
                ProjectilePositionAccess.TargetPosition(__instance) =
                    ProjectilePositionAccess.FreshDeterministicTarget(__instance);
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[ProjectilePos][ERR] frozen target normalization failed: " + e.Message);
            }
            finally
            {
                ProjectilePositionAccess.ExitBeforeLaunch();
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(AbilityProjectileAttackLineHelper), "TryGetTargetPointByRandomLocator")]
    internal static class AttackLine_RandomLocator_Deterministic_Patch
    {
        private static bool Prefix(ref bool __result, ref Vector3 result)
        {
            if (!MultiplayerCompatibility.SimulationFixesEnabled)
                return true;
            result = default(Vector3);
            __result = false;
            return false;
        }
    }
}

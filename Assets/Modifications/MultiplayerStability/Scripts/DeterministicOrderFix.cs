// Deterministic ordering for physics-broadphase unit queries (Channel-B audit rank 8).
//
// EntityBoundsHelper.FindUnitsInRange returns units in raw PhysicsScene2D.OverlapCircle order -- a function
// of each client's collider creation and toggle history. The order can differ across machines even when
// membership is identical. The sibling FindUnitsInShape method sorts its results with
// MechanicEntityHelper.ByIdComparison (EntityBoundsHelper.cs:182); FindUnitsInRange does not.
//
// Why it matters: PsychicPhenomenaRedirect picks its victim via list.Random(PFStatefulRandom.Mechanics) over
// this list. Both machines can consume the same hashed draw but resolve it to different units, so perils-of-
// the-warp damage lands on different entities while every RNG stream remains synchronized (structurally
// invisible to the LeakDetector). The ricochet candidate list and crossfire iteration ride the same order.
//
// Fix: one postfix applying the engine's own ByIdComparison (UniqueId ordinal -- the cross-machine-identical
// key) to FindUnitsInRange results in multiplayer. Pure reordering: membership unchanged, uniform Random
// distribution identical, count-only/iterate-all callers unaffected. Solo untouched (gated), fail-open.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.Controllers.Optimization;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Networking;
using UnityEngine;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(EntityBoundsHelper), nameof(EntityBoundsHelper.FindUnitsInRange),
        typeof(Vector3), typeof(float))]
    internal static class EntityBoundsHelper_FindUnitsInRange_DeterministicOrder_Patch
    {
        private static bool s_loggedActive;

        private static void Postfix(List<BaseUnitEntity> __result)
        {
            try
            {
                if (!NetworkingManager.IsMultiplayer || __result == null || __result.Count < 2)
                    return;
                // Comparison<Entity> is contravariant, so the engine's own comparer applies directly.
                __result.Sort(MechanicEntityHelper.ByIdComparison);
                if (!s_loggedActive)
                {
                    s_loggedActive = true;
                    MultiplayerStabilityMain.Log("[OrderFix] Active -- FindUnitsInRange results sorted by UniqueId in multiplayer.");
                }
            }
            catch (Exception)
            {
                // fail-open: unsorted = vanilla behaviour
            }
        }
    }
}

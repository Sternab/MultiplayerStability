// Party-pet spawner disposal repair.
//
// Paired v0.9.4 captures repeatedly recorded PartyPetSpawner.MyData.OnDispose dereferencing a
// SpawnedUnit reference that no longer resolved. The exception occurs before UnitSpawnerBase.MyData
// can run Clear(), leaving HasSpawned and the serialized unit reference stale. Both values are part
// of the spawner data hash.
//
// In exact-parity multiplayer, a missing BaseUnitEntity takes the base class's current cleanup path
// directly. A valid unit keeps the complete vanilla override, including the pet-following repair.
// Solo and unresolved sessions remain vanilla. This prevents the captured exception and stale
// spawner state; it does not claim that the paired, symmetric exceptions caused a recorded desync.
using System;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.View.Spawners;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(PartyPetSpawner.MyData), "OnDispose")]
    internal static class PartyPetSpawner_MyData_OnDispose_MissingUnit_Patch
    {
        private const int ContainmentLogLimit = 20;
        private static int s_containedCount;
        private static bool s_resolutionErrorLogged;

        private static bool Prefix(PartyPetSpawner.MyData __instance)
        {
            if (!MultiplayerCompatibility.SimulationFixesEnabled)
                return true;

            BaseUnitEntity spawnedUnit;
            try
            {
                spawnedUnit = __instance.SpawnedUnit.Get<BaseUnitEntity>();
            }
            catch (Exception e)
            {
                if (!s_resolutionErrorLogged)
                {
                    s_resolutionErrorLogged = true;
                    MultiplayerStabilityMain.LogNoThrow(
                        "[PartyPetFix][ERR] SpawnedUnit resolution failed; leaving vanilla active: "
                        + e.Message);
                }
                return true;
            }

            if (spawnedUnit != null)
                return true;

            // UnitSpawnerBase.MyData.OnDispose is Clear() followed by an empty Entity.OnDispose in
            // the supported build. Do not return to the derived method after this point: it would
            // repeat the confirmed null dereference before reaching the same cleanup.
            try
            {
                __instance.Clear();
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[PartyPetFix][ERR] base cleanup failed; surfacing once: " + e.Message);
                throw;
            }

            int count = ++s_containedCount;
            if (count <= ContainmentLogLimit)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[PartyPetFix] Cleared unresolved spawned-unit state (" + count + ").");
            }
            else if (count == ContainmentLogLimit + 1)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[PartyPetFix] Further unresolved spawned-unit cleanup logs suppressed.");
            }
            return false;
        }
    }
}

// Preview ghost fix -- facts applied to PREVIEW units must not draw from the hashed uuid stream, and
// preview units must not count as aura members.
//
// The game creates full unit copies for UI purposes -- inventory/character-screen dolls, level-up plan
// units (LevelUpPlanUnitHolder.RequestPlan) -- CLIENT-LOCALLY (only on the machine whose UI asked). Their
// CREATION is stream-safe (wrapped in DisableStatefulRandomContext), but UnitHelper.CopyInternal
// SUBSCRIBES the copy to the event bus at the end, so these ghosts keep REACTING to game events forever
// after: at combat start the pet system applies its control buffs to a preview pet, all-ally auras
// (Adept Joint Offence, Passive Learning) buff the preview player unit, etc. Those later fact attaches
// run OUTSIDE any safety context -> each mints a UniqueId from the hashed GlobalUuid stream -> the
// machine with the ghost draws extra -> permanent randomState fork at every combat start.
//
// Field-proven by capture 14 (2026-07-05, space combat, both v0.6.8): the ONLY asymmetric mints were
// 4 pet-system buffs on Master_ArbitesCyberMastiff_PetUnit[PREVIEW] / StartGame_Player_Unit[PREVIEW],
// host-only (+4 GlobalUuid draws at combat start) -- the same +4 signature as the earlier "Pascal fights
// always desync" captures (his all-ally auras were simply the loudest ghost-buffing system).
//
// Two patches:
//   A. EntityFact.Attach on a preview-owned fact runs inside DisableStatefulRandomContext in multiplayer:
//      the buff still applies (previews keep working for the UI), but its id comes from the non-hashed
//      fallback -- consistent with previews being entirely OUTSIDE hashed state (scene-entity dumps show
//      they are not hashed). Context released in a FINALIZER (postfixes are skipped on throw; an
//      unbalanced Request would make ALL stateful RNG nondeterministic -- the worst possible failure).
//   B. AreaEffectEntity.ShouldUnitBeInside returns false for preview units in multiplayer: ghosts must
//      not be aura MEMBERS at all -- membership also feeds count-scaled magnitudes (Hive World's
//      The More The Merrier), which would fork stat values even with stream-safe ids.
//
// Solo untouched (both patches gate on IsMultiplayer). Both machines should run the mod, though this fix
// degrades gracefully: it removes divergence sources on whichever machine has it.
using System;
using HarmonyLib;
using Kingmaker.ElementsSystem.ContextData;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Mechanics.Entities;
using Kingmaker.Networking;
using Kingmaker.Utility.StatefulRandom;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(EntityFact), nameof(EntityFact.Attach))]
    internal static class EntityFact_Attach_PreviewStreamSafe_Patch
    {
        private static bool s_loggedActive;

        // The owner is read from the `manager` PARAMETER, not __instance.Owner: Attach assigns the manager
        // INSIDE the call (EntityFact.cs:874) before minting the id (:877), so at prefix time
        // __instance.Owner is still null. v1 of this patch read __instance.Owner and silently never armed
        // (field-caught: capture 15 showed the +4 fork alive on 0.6.9 with zero [GhostFix] lines).
        private static void Prefix(EntityFactsManager manager, out IDisposable __state)
        {
            __state = null;
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return;
                var ownerUnit = (manager != null ? manager.Owner : null) as AbstractUnitEntity;
                if (ownerUnit == null || !ownerUnit.IsPreviewUnit)
                    return;
                __state = ContextData<DisableStatefulRandomContext>.Request();
                if (!s_loggedActive)
                {
                    s_loggedActive = true;
                    MultiplayerStabilityMain.Log("[GhostFix] Active -- preview-owned fact attaches are now hashed-stream-safe.");
                }
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.Log("[GhostFix][ERR] prefix: " + e.Message);
            }
        }

        private static void Finalizer(IDisposable __state)
        {
            __state?.Dispose();
        }
    }

    [HarmonyPatch(typeof(AreaEffectEntity), "ShouldUnitBeInside", typeof(BaseUnitEntity))]
    internal static class AreaEffectEntity_ShouldUnitBeInside_NoPreviews_Patch
    {
        private static bool Prefix(BaseUnitEntity unit, ref bool __result)
        {
            if (NetworkingManager.IsMultiplayer && unit != null && unit.IsPreviewUnit)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}

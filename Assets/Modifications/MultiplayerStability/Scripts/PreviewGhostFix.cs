// Preview ghost fix -- facts applied to PREVIEW units must not draw from the hashed uuid stream, and
// preview units must not count as aura members.
//
// The game creates full unit copies for UI purposes -- inventory/character-screen dolls and level-up plan
// units (LevelUpPlanUnitHolder.RequestPlan) -- only on the machine whose UI requested them. Their
// creation was partially stream-safe in vanilla (the DisableStatefulRandomContext scope closed before
// CopyItems, so preview items still minted hashed uuids, as recorded in capture 0.8.23 and addressed by the third
// patch below), and UnitHelper.CopyInternal
// subscribes the copy to the event bus at the end, so these copies continue reacting to game events:
// at combat start the pet system applies its control buffs to a preview pet, all-ally auras
// (Adept Joint Offence, Passive Learning) buff the preview player unit, etc. Those later fact attaches
// run outside any safety context. Each attach mints a UniqueId from the hashed GlobalUuid stream, so
// the machine with the preview copy advances the stream and creates a permanent randomState fork.
//
// Capture 14 (2026-07-05, space combat, both v0.6.8) recorded four asymmetric mints:
// 4 pet-system buffs on Master_ArbitesCyberMastiff_PetUnit[PREVIEW] / StartGame_Player_Unit[PREVIEW],
// host-only (+4 GlobalUuid draws at combat start) -- the same +4 signature as the earlier "Pascal fights
// always desync" captures.
//
// Three patches:
//   A. EntityFact.Attach on a preview-owned fact runs inside DisableStatefulRandomContext in multiplayer:
//      the buff still applies (previews keep working for the UI), but its id comes from the non-hashed
//      fallback, consistent with previews being outside hashed state (scene-entity dumps show
//      they are not hashed). Context is released in a finalizer so exceptions cannot leave
//      DisableStatefulRandomContext active.
//   B. AreaEffectEntity.ShouldUnitBeInside returns false for preview units in multiplayer: ghosts must
//      not be aura MEMBERS at all -- membership also feeds count-scaled magnitudes (Hive World's
//      The More The Merrier), which would fork stat values even with stream-safe ids.
//   C. (v0.8.24) The whole public UnitHelper.Copy(..., preview: true) holds the context in MP, closing
//      vanilla's own scope hole over CopyItems/CopyFacts/view creation.
//
// Solo untouched (all three patches gate on IsMultiplayer). Peer compatibility: exact parity required,
// like every sim/RNG-changing fix (see DESIGN_NOTES.md).
using System;
using HarmonyLib;
using Kingmaker.ElementsSystem.ContextData;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Mechanics.Entities;
using Kingmaker.Networking;
using Kingmaker.UnitLogic;
using Kingmaker.Utility.StatefulRandom;

namespace MultiplayerStability
{
    // Third half (v0.8.24, capture 0.8.23): vanilla's own preview scope has a hole -- UnitHelper.CopyInternal
    // holds DisableStatefulRandomContext.RequestIf(preview) only around unit creation (:101), but the
    // subsequent CopyItems call (:128) runs AFTER that using closes, so preview ITEM creation mints hashed
    // GlobalUuid ids. One peer building more preview batches than the other = the captured creation-count
    // fork. Fix: in MP, hold the context across the WHOLE public Copy(..., preview: true) operation
    // (vanilla's inner nested request is unaffected). Solo keeps vanilla's exact scope.
    [HarmonyPatch(typeof(UnitHelper), nameof(UnitHelper.Copy),
        typeof(BaseUnitEntity), typeof(bool), typeof(bool), typeof(bool), typeof(bool))]
    internal static class UnitHelper_Copy_FullPreviewScope_Patch
    {
        private static bool s_loggedActive;

        private static void Prefix(bool preview, out IDisposable __state)
        {
            __state = null;
            try
            {
                if (!NetworkingManager.IsMultiplayer || !preview)
                    return;
                __state = ContextData<DisableStatefulRandomContext>.Request();
                if (!s_loggedActive)
                {
                    s_loggedActive = true;
                    MultiplayerStabilityMain.Log("[GhostFix] Full preview-copy scope active -- preview item creation no longer mints hashed uuids in multiplayer.");
                }
            }
            catch (Exception)
            {
                // fail-open: vanilla scope
            }
        }

        private static Exception Finalizer(IDisposable __state, Exception __exception)
        {
            try
            {
                __state?.Dispose();
            }
            catch (Exception e)
            {
                // A stuck DisableStatefulRandomContext would contaminate EVERY subsequent hashed draw --
                // this failure must not pass silently.
                MultiplayerStabilityMain.Log("[GhostFix][ERR] DisableStatefulRandomContext dispose FAILED -- stateful RNG may be stuck non-deterministic: " + e);
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(EntityFact), nameof(EntityFact.Attach))]
    internal static class EntityFact_Attach_PreviewStreamSafe_Patch
    {
        private static bool s_loggedActive;

        // The owner is read from the `manager` PARAMETER, not __instance.Owner: Attach assigns the manager
        // INSIDE the call (EntityFact.cs:874) before minting the id (:877), so at prefix time
        // __instance.Owner is still null. v1 of this patch read __instance.Owner and never activated
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

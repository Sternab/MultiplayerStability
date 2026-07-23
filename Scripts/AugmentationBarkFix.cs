// Augmentation-screen bark containment (capture 0.8.23, Codex round 27; player-bucket fork confirmed).
//
// The Star System augmentation screen (AugmentationsVM ctor, :99/:111) picks a cosmetic bark with
// UnityEngine.Random.Range and raises IBarkBanterPlayedHandler. BarkBanterController.HandleBarkBanter (:49)
// then does Game.Instance.Player.PlayedBanters.Add(banter) -- and PlayedBanters is in the synchronized
// Player hash. The screen is CLIENT-LOCAL (only the machine browsing augmentations runs any of this), so
// the write is one-sided by construction -- the client-random pick just varies which banter poisons the
// hash. The playing banter also draws the hashed Bark stream.
//
// Containment is CALLER-specific (the invariant-beats-provenance lesson inverted: here the OPERATION is
// legitimate for sim-side raisers -- ShowBanter etudes, system-map objects raise the same event
// symmetrically on all machines and MUST keep marking banters played -- and illegitimate only for this UI
// caller): a flag brackets the AugmentationsVM constructor, and HandleBarkBanter skips while it is set in
// MP. Cost in co-op: the augmentation screen plays no bark banter (it was never synced content -- the pick
// was client-random anyway). Solo untouched; sim-side banters untouched everywhere.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.BarkBanters;
using Kingmaker.Code.UI.MVVM.VM.ServiceWindows.Augmentations;
using Kingmaker.Controllers;
using Kingmaker.Networking;

namespace MultiplayerStability
{
    internal static class AugmentationBarkFix
    {
        // Depth counter, not a boolean (Codex round 28): a lone flag cleared unconditionally would break
        // under re-entrant construction or future constructor chaining. Depth is nesting-safe by shape.
        internal static int s_augVmDepth;

        [HarmonyPatch]
        internal static class AugmentationsVM_Ctor_Flag_Patch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var ctors = AccessTools.GetDeclaredConstructors(typeof(AugmentationsVM));
                if (ctors == null || ctors.Count == 0)
                {
                    MultiplayerStabilityMain.Log("[BarkFix][ERR] AugmentationsVM constructor not found -- containment inactive.");
                    yield break;
                }
                foreach (var c in ctors)
                    yield return c;
            }

            private static void Prefix()
            {
                s_augVmDepth++;
            }

            // Finalizer, not postfix: the depth must unwind even if the ctor throws.
            private static Exception Finalizer(Exception __exception)
            {
                if (s_augVmDepth > 0)
                    s_augVmDepth--;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(BarkBanterController), nameof(BarkBanterController.HandleBarkBanter),
            typeof(BlueprintBarkBanter))]
        internal static class HandleBarkBanter_ContainUiCaller_Patch
        {
            private static bool s_loggedActive;

            private static bool Prefix()
            {
                try
                {
                    if (!NetworkingManager.IsMultiplayer || s_augVmDepth == 0)
                        return true;                             // solo, or a legitimate sim-side raiser
                    if (!s_loggedActive)
                    {
                        s_loggedActive = true;
                        MultiplayerStabilityMain.Log("[BarkFix] Active -- augmentation-screen barks no longer write hashed player state in multiplayer.");
                    }
                    return false;                                // skip: no PlayedBanters add, no bark player
                }
                catch (Exception)
                {
                    return true;                                 // fail-open: vanilla behaviour
                }
            }
        }
    }
}

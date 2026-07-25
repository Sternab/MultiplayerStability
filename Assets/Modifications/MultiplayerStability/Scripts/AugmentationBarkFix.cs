// Augmentation-screen bark containment (capture 0.8.23; player-bucket fork confirmed).
//
// The Star System augmentation screen (AugmentationsVM ctor, :99/:111) picks a cosmetic bark with
// UnityEngine.Random.Range and raises IBarkBanterPlayedHandler. BarkBanterController.HandleBarkBanter (:49)
// then does Game.Instance.Player.PlayedBanters.Add(banter) -- and PlayedBanters is in the synchronized
// Player hash. The screen is client-local, so only the machine browsing augmentations performs this
// write. The random pick determines which divergent banter value is stored. Playing the banter also
// advances the hashed Bark stream.
//
// Containment is caller-specific because simulation-side raisers, including ShowBanter etudes and
// system-map objects, use the same event legitimately on all peers. A flag brackets the
// AugmentationsVM constructor, and HandleBarkBanter skips only while that flag is set in multiplayer.
// The augmentation screen plays no bark in co-op. Solo and simulation-side banters are unchanged.
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
        // A depth counter remains correct under re-entrant construction or constructor chaining.
        internal static int s_augVmDepth;

        [HarmonyPatch]
        internal static class AugmentationsVM_Ctor_Flag_Patch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var ctors = AccessTools.GetDeclaredConstructors(typeof(AugmentationsVM));
                if (ctors == null || ctors.Count == 0)
                    throw new MissingMethodException(
                        "AugmentationsVM constructor not found; bark containment inactive.");
                return ctors;
            }

            private static void Prefix(out bool __state)
            {
                __state = MultiplayerCompatibility.SimulationFixesEnabled;
                if (__state)
                    s_augVmDepth++;
            }

            // Finalizer, not postfix: the depth must unwind even if the ctor throws.
            private static Exception Finalizer(bool __state, Exception __exception)
            {
                if (__state && s_augVmDepth > 0)
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
                if (!MultiplayerCompatibility.SimulationFixesEnabled || s_augVmDepth == 0)
                    return true;                                 // vanilla or legitimate sim-side raiser
                if (!s_loggedActive)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[BarkFix] Active; augmentation-screen barks cannot write synchronized state.");
                    s_loggedActive = true;
                }
                return false;                                    // logging cannot re-enable the skipped write
            }
        }
    }
}

// Dialog RNG fix for view-driven draws from the hashed DialogSystem stream
// (captures 0.8.7 and two-sided 0.8.8).
//
// CueSelection.Select() with Strategy.Random draws PFStatefulRandom.DialogSystem.Range (CueSelection.cs:42)
// to pick a cue. The same Select() is called both from synchronized advancement and from view code:
// BlueprintAnswer.SkillChecks (:117, the answer UI reading the next cue to render its check icon, fired by
// DialogAnswerBaseView.UpdateView on every refresh) and CanSelect's RequireValidCue check (:262). Because a
// hashed stream's state depends only on how many times it is drawn, a view-time draw on one machine but not
// the other forks the randomState hash.
//
// The v0.8.8 implementation had two gaps:
//  1. Scope: the capture's draw came from BlueprintAnswer.SkillChecksDC ->
//     CharacterSelection.SelectUnit(Random) -> DialogSystem.Range -- a second DialogSystem consumer the
//     0.8.8 fix deliberately left unpatched on the assumption it only ran from the in-tick SelectAnswer
//     path. The UI also calls it repeatedly to preview which party member performs a
//     check (the repeated client-only Cue_0007 condition logs are that preview running).
//  2. Discriminator: the guard fired only when !RealTimeController.IsSimulationTick, but preview work
//     can execute during a simulation tick. Preview frequency is client-local, so caller identity,
//     rather than tick timing, must determine whether a draw is a preview.
//
// Current implementation:
//
//  A. Wrap the two UI-preview getters, BlueprintAnswer.SkillChecks (:109) and SkillChecksDC (:131),
//     in DisableStatefulRandomContext in multiplayer. The mechanical SelectUnit call
//     (DialogController.SelectAnswer :745) is untouched and keeps its
//     synchronized hashed draw.
//  C. Wrap DialogController.HasNextUnselectedAnswers, a view-only answer-tree inspection path
//     identified by the Solomorne capture.
//
// Guard B, a deterministic first-eligible CueSelection replacement, shipped in v0.8.9-v0.8.14 and
// was removed in v0.8.15 because it also changed synchronized narrative cue selection.
//
// Solo is unchanged. Context release uses finalizers so an exception cannot leave
// DisableStatefulRandomContext active.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Controllers.Dialog;
using Kingmaker.DialogSystem.Blueprints;
using Kingmaker.ElementsSystem.ContextData;
using Kingmaker.Networking;
using Kingmaker.Utility.StatefulRandom;

namespace MultiplayerStability
{
    // Guard A: the two UI-preview getters hold DisableStatefulRandomContext for their whole body in MP, so
    // their internal draws (CharacterSelection.SelectUnit Random pick; any incidental preview-rule work)
    // divert to the non-hashed fallback on every machine identically -- semantic, not timing-based.
    [HarmonyPatch]
    internal static class BlueprintAnswer_PreviewGetters_NoHashedDraw_Patch
    {
        private static bool s_loggedActive;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var prop in new[] { "SkillChecks", "SkillChecksDC" })
            {
                var getter = AccessTools.PropertyGetter(typeof(BlueprintAnswer), prop);
                if (getter != null)
                    yield return getter;
                else
                    MultiplayerStabilityMain.Log("[DialogRng][ERR] BlueprintAnswer." + prop + " getter not found -- preview path unguarded.");
            }
        }

        private static void Prefix(out IDisposable __state)
        {
            __state = null;
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return;
                __state = ContextData<DisableStatefulRandomContext>.Request();
                if (!s_loggedActive)
                {
                    s_loggedActive = true;
                    MultiplayerStabilityMain.Log("[DialogRng] Preview guard active -- answer skill-check previews no longer advance hashed streams in multiplayer.");
                }
            }
            catch (Exception)
            {
                // fail-open: no context held -> vanilla behaviour
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
                MultiplayerStabilityMain.Log("[DialogRng][ERR] DisableStatefulRandomContext dispose FAILED -- stateful RNG may be stuck non-deterministic: " + e);
            }
            return __exception;
        }
    }

    // Guard C (v0.8.20, capture 0.8.19): AnswerVM.UpdateView calls
    // DialogController.HasNextUnselectedAnswers -> ...Internal -> CueSelection.Select. The public
    // HasNextUnselectedAnswers(BlueprintAnswer) method is a UI-inspection API whose only external caller
    // is AnswerVM (:93). Dialog advancement does not use this entry point, so the whole-body wrap does
    // not change synchronized cue selection.
    [HarmonyPatch(typeof(DialogController), nameof(DialogController.HasNextUnselectedAnswers), typeof(BlueprintAnswer))]
    internal static class DialogController_HasNextUnselectedAnswers_NoHashedDraw_Patch
    {
        private static bool s_loggedActive;

        private static void Prefix(out IDisposable __state)
        {
            __state = null;
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return;
                __state = ContextData<DisableStatefulRandomContext>.Request();
                if (!s_loggedActive)
                {
                    s_loggedActive = true;
                    MultiplayerStabilityMain.Log("[DialogRng] Inspection guard active -- answer-tree inspection (HasNextUnselectedAnswers) no longer advances hashed streams in multiplayer.");
                }
            }
            catch (Exception)
            {
                // fail-open: no context held -> vanilla behaviour
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
                MultiplayerStabilityMain.Log("[DialogRng][ERR] DisableStatefulRandomContext dispose FAILED -- stateful RNG may be stuck non-deterministic: " + e);
            }
            return __exception;
        }
    }

    // Guard B (deterministic first-eligible CueSelection in MP) was removed in v0.8.15.
    // CueSelection.Select serves narrative progression -- first cues, answer continuations, and sequence
    // exits (DialogController :365/:785/:1086) -- so forcing first-eligible changed story selection.
    // Synchronized random draws remain unchanged. Any future view-time call site should be handled by
    // wrapping that caller, not by replacing CueSelection globally.
}

// Dialog RNG fix -- a hashed DialogSystem draw whose COUNT is view-driven (field-proven, captures 0.8.7 +
// two-sided 0.8.8).
//
// CueSelection.Select() with Strategy.Random draws PFStatefulRandom.DialogSystem.Range (CueSelection.cs:42)
// to pick a cue. The same Select() is called both from real sim-ticked advancement AND from pure-VIEW code:
// BlueprintAnswer.SkillChecks (:117, the answer UI reading the next cue to render its check icon, fired by
// DialogAnswerBaseView.UpdateView on every refresh) and CanSelect's RequireValidCue check (:262). Because a
// hashed stream's state depends only on how many times it is drawn, a view-time draw on one machine but not
// the other forks the randomState hash.
//
// WHY THE v0.8.8 ATTEMPT FAILED (two holes, both proven by the two-sided 0.8.8 capture; do not reintroduce):
//  1. Wrong scope: the capture's actual drawer was BlueprintAnswer.SkillChecksDC ->
//     CharacterSelection.SelectUnit(Random) -> DialogSystem.Range -- a second DialogSystem consumer the
//     0.8.8 fix deliberately left unpatched on the assumption it only ran from the in-tick SelectAnswer
//     path. The capture disproved that: the UI calls it repeatedly to preview which party member performs a
//     check (the repeated client-only Cue_0007 condition logs are that preview running).
//  2. Broken discriminator: the guard fired only when !RealTimeController.IsSimulationTick, but preview
//     work can execute WHILE a simulation tick is processing, so in-tick preview draws passed the gate as
//     "legitimate". Preview frequency is client-local (whose UI refreshes, hovers, re-evaluates), so draw
//     COUNTS differ per machine -- host drew 0 times, client 5. Nothing timing-based can distinguish
//     view-preview from sim draws; the discriminator must be SEMANTIC (which caller), not temporal.
//
// FIX -- two complementary, timing-independent guards (Codex-reviewed shape, two-sided capture 0.8.8):
//
//  A. SEMANTIC WRAP of the two UI-preview getters, BlueprintAnswer.SkillChecks (:109) and SkillChecksDC
//     (:131), in DisableStatefulRandomContext in MP. These getters are preview-by-definition regardless of
//     which phase happens to be executing (the 0.8.8 capture proved they can run with IsSimulationTick ==
//     true), so wrapping the CALLER is correct where wrapping by TIMING was not. This covers the capture's
//     actual drawer -- SkillChecksDC -> CharacterSelection.SelectUnit(Random) -> DialogSystem.Range -- which
//     the earlier scope note wrongly assumed was in-tick-only (assumption FALSIFIED by capture 0.8.8). The
//     real mechanical SelectUnit call (DialogController.SelectAnswer :745) is untouched and keeps its
//     synchronized hashed draw.
//  B. (REMOVED v0.8.15 -- see the note at the bottom of this file.) A deterministic first-eligible
//     CueSelection replacement shipped briefly (v0.8.9-v0.8.14) but changed REAL narrative cue selection in
//     co-op, not just banter previews; withdrawn in favor of Guard A alone, which covers the only
//     capture-convicted leak path. Vanilla's in-tick Random cue draws are synced and correct.
//
// Solo is vanilla (prefix no-ops); fail-open; context release via FINALIZER (never a postfix -- an
// unbalanced Request would make ALL stateful RNG nondeterministic).
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

    // Guard C (v0.8.20, capture 0.8.19 -- the Solomorne dialogue fork; the LeakDetector caught six
    // out-of-tick DialogSystem draws as the dialogue opened, the exact contingency the note below reserves):
    // the THIRD convicted view-time caller is AnswerVM.UpdateView -> DialogController.HasNextUnselectedAnswers
    // -> ...Internal -> CueSelection.Select. The public HasNextUnselectedAnswers(BlueprintAnswer) is a pure
    // UI-inspection API -- its only external caller is AnswerVM (:93); the dialog's real advancement never
    // enters through it -- so the semantic whole-body wrap is valid regardless of tick phase, same shape as
    // Guard A. The internal recursion is private and only reachable through this entry.
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

    // Guard B (deterministic first-eligible CueSelection in MP) was REMOVED in v0.8.15 (external review):
    // CueSelection.Select serves REAL narrative progression -- first cues, answer continuations, sequence
    // exits (DialogController :365/:785/:1086) -- so forcing first-eligible changed actual story-content
    // selection in co-op, a real behavior cost carried as "insurance" against stray view-time callers that
    // no capture has ever convicted. The in-tick Random draws are synced and correct in vanilla; Guard A
    // covers the only proven view-time path (the preview getters). Reinstate a CueSelection guard ONLY if a
    // future capture shows DialogSystem diverging with Guard A active -- and then as a semantic wrap of the
    // convicted CALLER, never a global selection change.
}

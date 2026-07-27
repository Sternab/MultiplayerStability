// Synchronized dialogue-answer command fix.
//
// A paired v0.9.2 capture showed the host execute Answer_0001 and Cue_4 while the client executed
// neither, immediately before a persistent fork. DialogAnswerGameCommand is synchronized, but its
// ExecuteInternal silently returns when either CuePlayScheduled is true locally or the command's
// captured cue tick is older than CurrentCueUpdateTick. Those fields are local transient controller
// state, not command identity, and Dark Heresy retains the same two early returns.
//
// Under exact-build compatibility, an answer command is accepted when its answer is still offered
// by the peer's current cue. The first command for that concrete cue occurrence wins; later commands
// for the same cue occurrence are ignored, preserving the duplicate protection previously supplied
// by CuePlayScheduled. Commands that pass vanilla's guards still execute through vanilla unchanged.
// Only a command that vanilla would silently drop is re-routed through SelectAnswer.
//
// Manual-character answers retain vanilla behavior because they use a separate selection path.
// Reflection drift declines the patch. A real SelectAnswer exception is allowed to surface once and
// is never retried through a fail-open path after partial narrative mutation.
using System;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Controllers.Dialog;
using Kingmaker.DialogSystem;
using Kingmaker.DialogSystem.Blueprints;
using Kingmaker.GameCommands;

namespace MultiplayerStability
{
    internal static class DialogAnswerCommandFix
    {
        private static object s_lastAcceptedCue;
        private static int s_lastAcceptedCueTick = int.MinValue;

        internal static void ResetState()
        {
            s_lastAcceptedCue = null;
            s_lastAcceptedCueTick = int.MinValue;
        }

        private static bool WasAccepted(object cue, int cueTick)
            => ReferenceEquals(s_lastAcceptedCue, cue)
                && s_lastAcceptedCueTick == cueTick;

        private static void RecordAccepted(object cue, int cueTick)
        {
            s_lastAcceptedCue = cue;
            s_lastAcceptedCueTick = cueTick;
        }

        [HarmonyPatch(typeof(DialogAnswerGameCommand), "ExecuteInternal")]
        internal static class DialogAnswerGameCommand_StableAcceptance_Patch
        {
            private sealed class AcceptedCue
            {
                internal DialogController Controller;
                internal object Cue;
                internal int CueTick;
            }

            private static AccessTools.FieldRef<DialogAnswerGameCommand, int> s_commandTick;
            private static AccessTools.FieldRef<DialogAnswerGameCommand, string> s_answer;
            private static bool s_loggedActive;
            private static int s_overrideLogs;
            private static int s_duplicateLogs;

            private static bool Prepare()
            {
                try
                {
                    s_commandTick =
                        AccessTools.FieldRefAccess<DialogAnswerGameCommand, int>("m_Tick");
                    s_answer =
                        AccessTools.FieldRefAccess<DialogAnswerGameCommand, string>("m_Answer");
                    return s_commandTick != null && s_answer != null;
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[DialogCommandFix][ERR] command fields not resolvable; patch declined: "
                        + e.Message);
                    return false;
                }
            }

            private static bool Prefix(
                DialogAnswerGameCommand __instance,
                out AcceptedCue __state)
            {
                __state = null;
                if (!MultiplayerCompatibility.SimulationFixesEnabled)
                    return true;

                DialogController controller;
                object cue;
                int cueTick;
                int commandTick;
                string answerGuid;
                BlueprintAnswer offered = null;
                bool cueGuard;
                bool tickGuard;
                try
                {
                    controller = Game.Instance?.DialogController;
                    cue = controller?.CurrentCue;
                    if (controller == null || cue == null)
                        return true;

                    cueTick = controller.CurrentCueUpdateTick;
                    commandTick = s_commandTick(__instance);
                    answerGuid = s_answer(__instance);
                    foreach (var answer in controller.Answers)
                    {
                        if (answer != null && answer.AssetGuid == answerGuid)
                        {
                            offered = answer;
                            break;
                        }
                    }

                    if (offered == null
                        || offered.CharacterSelection.SelectionType
                            == CharacterSelection.Type.Manual)
                    {
                        return true;
                    }

                    LogActiveOnce();
                    if (WasAccepted(cue, cueTick))
                    {
                        LogDuplicate(cueTick, answerGuid);
                        return false;
                    }

                    cueGuard = controller.CuePlayScheduled;
                    tickGuard = commandTick < cueTick;
                    if (!cueGuard && !tickGuard)
                    {
                        __state = new AcceptedCue
                        {
                            Controller = controller,
                            Cue = cue,
                            CueTick = cueTick
                        };
                        return true;
                    }
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[DialogCommandFix][ERR] preflight failed; command uses vanilla: "
                        + e.Message);
                    return true;
                }

                // This is the only mutating operation in the override path. Keep it outside the
                // fail-open preflight so a real exception cannot rerun partially applied dialogue work.
                controller.SelectAnswer(answerGuid);
                RecordAccepted(cue, cueTick);
                LogOverride(cueTick, commandTick, answerGuid, cueGuard, tickGuard);
                return false;
            }

            private static void Postfix(AcceptedCue __state)
            {
                if (__state == null)
                    return;
                try
                {
                    // A successful SelectAnswer always schedules its next cue, including a null exit cue.
                    // Do not consume the per-cue latch if vanilla rejected the answer before that point.
                    if (__state.Controller.CuePlayScheduled)
                        RecordAccepted(__state.Cue, __state.CueTick);
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[DialogCommandFix][ERR] accepted-command bookkeeping failed: "
                        + e.Message);
                }
            }

            private static void LogActiveOnce()
            {
                if (s_loggedActive)
                    return;
                try
                {
                    MultiplayerStabilityMain.Log(
                        "[DialogCommandFix] Current-offered-answer acceptance active.");
                    s_loggedActive = true;
                }
                catch (Exception)
                {
                }
            }

            private static void LogOverride(
                int cueTick,
                int commandTick,
                string answer,
                bool cueGuard,
                bool tickGuard)
            {
                if (s_overrideLogs >= 20)
                    return;
                s_overrideLogs++;
                MultiplayerStabilityMain.LogNoThrow(
                    "[DialogCommandFix] Accepted offered synchronized answer despite local guard"
                    + " state: cueTick=" + cueTick
                    + " commandTick=" + commandTick
                    + " answer=" + answer
                    + " cueScheduled=" + cueGuard
                    + " staleTick=" + tickGuard
                    + (s_overrideLogs == 20 ? " (further overrides omitted)" : ""));
            }

            private static void LogDuplicate(int cueTick, string answer)
            {
                if (s_duplicateLogs >= 10)
                    return;
                s_duplicateLogs++;
                MultiplayerStabilityMain.LogNoThrow(
                    "[DialogCommandFix] Ignored duplicate answer command for cueTick="
                    + cueTick + " answer=" + answer
                    + (s_duplicateLogs == 10 ? " (further duplicates omitted)" : ""));
            }
        }
    }
}

// Cooperative pause consensus fix.
//
// A paired v0.9.2 capture recorded the only unmatched accepted Pause transition of the session
// immediately after an area load. The synchronized RequestPauseGameCommand ran on both peers, but
// PauseController.IsPausedByPlayers compares m_PausedPlayer with NetworkingManager.PlayersReadyMask.
// That mask is rebuilt from each peer's local ActivePlayers list, which can differ briefly while a
// transferred save finishes loading. One requester can therefore satisfy "all ready players" on one
// peer and not the other.
//
// Exact-parity multiplayer uses the actor roster already validated by MultiplayerCompatibility for
// pause consensus. NetPlayer indices follow the same sorted actor order, so every peer tests the same
// participant bits throughout the transfer epoch. Departing actors are removed by their stable start
// index. Solo, mixed, unresolved, and reflection-drift paths retain vanilla behavior.
//
// Dark Heresy independently blocks PauseController.RequestPauseUi while a loading screen is active.
// The first patch backports that narrow request guard using Rogue Trader's equivalent loading flags.
// The roster patch also covers the post-hide interval in which the loading flags have cleared but
// ActivePlayers has not yet converged.
using System;
using HarmonyLib;
using Kingmaker.Controllers;
using Kingmaker.EntitySystem.Persistence;
using Kingmaker.Networking;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(PauseController), nameof(PauseController.RequestPauseUi))]
    internal static class PauseController_RequestPauseUi_LoadGuard_Patch
    {
        private static bool s_loggedSuppression;

        private static bool Prefix()
        {
            if (!MultiplayerCompatibility.SimulationFixesEnabled)
                return true;

            bool loading;
            try
            {
                var process = LoadingProcess.Instance;
                loading = process != null
                    && (process.IsLoadingInProcess
                        || process.IsLoadingScreenActive
                        || process.IsManualLoadingScreenActive);
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[PauseFix][ERR] loading state unavailable; pause request uses vanilla: "
                    + e.Message);
                return true;
            }

            if (!loading)
                return true;

            if (!s_loggedSuppression)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[PauseFix] Ignored a local pause request while loading.");
                s_loggedSuppression = true;
            }
            return false;
        }
    }

    [HarmonyPatch(
        typeof(PauseController),
        nameof(PauseController.IsPausedByPlayers),
        MethodType.Getter)]
    internal static class PauseController_IsPausedByPlayers_EpochRoster_Patch
    {
        private static AccessTools.FieldRef<PauseController, NetPlayerGroup> s_pausedPlayers;
        private static bool s_loggedActive;
        private static int s_correctionLogs;

        private static bool Prepare()
        {
            try
            {
                s_pausedPlayers =
                    AccessTools.FieldRefAccess<PauseController, NetPlayerGroup>("m_PausedPlayer");
                return s_pausedPlayers != null;
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[PauseFix][ERR] m_PausedPlayer not resolvable; roster fix declined: "
                    + e.Message);
                return false;
            }
        }

        private static void Postfix(PauseController __instance, ref bool __result)
        {
            if (!MultiplayerCompatibility.TryGetPauseParticipantMask(
                out NetPlayerGroup participants))
            {
                return;
            }

            bool vanilla = __result;
            bool stable;
            try
            {
                stable = s_pausedPlayers(__instance).Contains(participants);
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[PauseFix][ERR] stable pause evaluation failed; vanilla result remains: "
                    + e.Message);
                return;
            }

            __result = stable;
            if (!s_loggedActive)
            {
                try
                {
                    MultiplayerStabilityMain.Log(
                        "[PauseFix] Exact transfer-roster pause consensus active.");
                    s_loggedActive = true;
                }
                catch (Exception)
                {
                }
            }
            if (vanilla != stable && s_correctionLogs < 20)
            {
                s_correctionLogs++;
                MultiplayerStabilityMain.LogNoThrow(
                    "[PauseFix] Corrected local ready-mask verdict from " + vanilla
                    + " to " + stable + " (epoch=" + participants
                    + ", local-ready=" + NetworkingManager.PlayersReadyMask + ")."
                    + (s_correctionLogs == 20 ? " Further corrections are omitted." : ""));
            }
        }
    }
}

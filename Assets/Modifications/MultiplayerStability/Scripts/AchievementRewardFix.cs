// Cabin achievement-reward containment.
//
// Paired v0.9.1 logs recorded the first persistent mismatch when RTCabinVisited started. Both peers
// created the same three ordinary cabin rewards, but only the peer whose local platform profile had
// unlocked 67_ThereIsOnlyWar created AmuletOfTheIndomitableSpirit_item. That extra item also
// consumed a GlobalUuid on that peer.
//
// The game-build 1.6.1.514 blueprint pack confirms the complete content path in
// RTCabinExit_Visible:
//   IsAchievementUnlocked$7810c998cd844ca59ed6eda7e000828c
//     -> AddItemsToCollection$4b2873a33dea4a26a4087d8941f10238
//     -> PlayerChest / AmuletOfTheIndomitableSpirit_item
//
// AchievementsManager synchronizes each machine with its own Steam, GOG, or EGS profile. That
// client-local result is valid for achievements, but it cannot safely decide a shared inventory
// write in deterministic multiplayer.
//
// The narrow containment skips only the convicted AddItemsToCollection action while exact-build
// simulation fixes are active. Other achievement checks and inventory actions are untouched. Solo
// retains vanilla behavior. The deliberate multiplayer cost is that this future-playthrough amulet
// is not granted during co-op; preserving the save owner's entitlement would require a separate
// synchronized, owner-authoritative reward decision.
using System;
using HarmonyLib;
using Kingmaker.Designers.EventConditionActionSystem.Actions;

namespace MultiplayerStability
{
    internal static class AchievementRewardFix
    {
        private const string CabinAmuletRewardActionGuid =
            "4b2873a33dea4a26a4087d8941f10238";

        [HarmonyPatch(typeof(AddItemsToCollection), "RunAction")]
        internal static class CabinAmuletReward_Patch
        {
            private static bool s_loggedSuppression;

            private static bool Prefix(AddItemsToCollection __instance)
            {
                if (!MultiplayerCompatibility.SimulationFixesEnabled)
                    return true;

                string actionGuid;
                try
                {
                    actionGuid = __instance?.AssetGuid;
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[AchievementRewardFix][ERR] Could not identify an AddItemsToCollection "
                        + "action; vanilla behavior remains active: " + e.Message);
                    return true;
                }

                if (!string.Equals(
                    actionGuid,
                    CabinAmuletRewardActionGuid,
                    StringComparison.Ordinal))
                {
                    return true;
                }

                LogSuppressionOnce();
                return false;
            }

            private static void LogSuppressionOnce()
            {
                if (s_loggedSuppression)
                    return;

                try
                {
                    MultiplayerStabilityMain.Log(
                        "[AchievementRewardFix] Suppressed the local-profile cabin amulet reward "
                        + "to protect shared inventory state.");
                    s_loggedSuppression = true;
                }
                catch
                {
                    // Suppression is independent of logging; retry the signal on the next call.
                }
            }
        }
    }
}

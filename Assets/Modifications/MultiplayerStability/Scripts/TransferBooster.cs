// Faster co-op save transfers. Vanilla moves ~0.22 MB/s on a 27 ms connection because the transfer is
// gated twice: Photon's flow-control window (SequenceDeltaLimitSends = 75 reliable ~1.2 KB fragments,
// ~86 KB in flight) only refills when acks reach the sender, and acks only go out once per rendered
// frame -- transfers run during loading screens at a few FPS. Two levers, measured 2026-07-02:
//
//   1. Ack pump: while a transfer runs, a background timer calls SendAcksOnly() every few ms so the
//      window turns over at network speed instead of frame speed. Same timer-thread pattern the game
//      itself uses (Kingmaker.Networking.Tools.BackgroundPing). Client-local and safe on any subset of
//      players; on the receiving side it is also what keeps the relay's per-client queue draining.
//   2. Larger application window: larger chunks + deeper send window (96 KB x 4 vs vanilla 48 KB x 3).
//      Applied only when every player in the room runs this mod: without fast receiver acks the Photon server
//      buffers the excess and force-disconnects the receiver (observed live: 6x192 KB burst ->
//      DisconnectByDisconnectMessage), so the boost is gated on the peer mod list.
using System;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Kingmaker.Networking;
using Photon.Realtime;

namespace MultiplayerStability
{
    internal static class TransferBooster
    {
        private const int BoostedChunkBytes = 96 * 1024;
        private const int BoostedStreams = 4;
        private const int AckPumpIntervalMs = 8;

        // m_LoadBalancingClient is private; everything on it we touch (LoadBalancingPeer.SendAcksOnly)
        // is public.
        private static readonly AccessTools.FieldRef<PhotonManager, LoadBalancingClient> ClientRef =
            AccessTools.FieldRefAccess<PhotonManager, LoadBalancingClient>("m_LoadBalancingClient");

        private static readonly object Sync = new object();
        private static Timer s_AckPump;
        private static LoadBalancingClient s_PumpClient;
        private static int s_ActiveTransfers;
        private static int s_VanillaChunkBytes;
        private static int s_VanillaStreams;
        private static bool s_Boosted;

        internal static void OnTransferStarting(string kind)
        {
            lock (Sync)
            {
                s_ActiveTransfers++;
                if (s_ActiveTransfers > 1)
                    return;

                bool allModded = EveryPlayerRunsMod();
                if (allModded && !s_Boosted)
                {
                    s_VanillaChunkBytes = SaveMetaData.MaxPacketSize;
                    s_VanillaStreams = StreamsController.DefaultStreamsCount;
                    SaveMetaData.MaxPacketSize = BoostedChunkBytes;
                    StreamsController.DefaultStreamsCount = BoostedStreams;
                    s_Boosted = true;
                }

                StartAckPumpLocked();
                MultiplayerStabilityMain.Log(string.Format(
                    "[Transfer] {0} starting: chunk={1}KB streams={2} ackPumpMs={3} allPlayersModded={4}",
                    kind, SaveMetaData.MaxPacketSize / 1024, StreamsController.DefaultStreamsCount,
                    AckPumpIntervalMs, allModded));
            }
        }

        // Called from a finalizer so a synchronous throw in UploadSave/DownloadSave (e.g.
        // AlreadyInProgressException) cannot leak the transfer count or the pump.
        internal static void OnTransferReturned(Task transferTask, Exception exception, string kind)
        {
            if (exception != null || transferTask == null)
            {
                OnTransferEnded(kind + " (threw)");
                return;
            }
            transferTask.ContinueWith(
                t => OnTransferEnded(kind + (t.IsFaulted ? " (faulted)" : t.IsCanceled ? " (canceled)" : " (completed)")),
                TaskScheduler.Default);
        }

        private static void OnTransferEnded(string kind)
        {
            lock (Sync)
            {
                if (s_ActiveTransfers > 0)
                    s_ActiveTransfers--;
                if (s_ActiveTransfers > 0)
                    return;

                if (s_Boosted)
                {
                    SaveMetaData.MaxPacketSize = s_VanillaChunkBytes;
                    StreamsController.DefaultStreamsCount = s_VanillaStreams;
                    s_Boosted = false;
                }
                StopAckPumpLocked();
                MultiplayerStabilityMain.Log("[Transfer] " + kind + "; vanilla values restored, ack pump stopped.");
            }
        }

        private static bool EveryPlayerRunsMod()
        {
            try
            {
                var photon = PhotonManager.Instance;
                if (photon == null)
                    return false;
                foreach (var playerInfo in photon.ActivePlayers)
                {
                    ModData[] mods;
                    if (!PhotonManager.Mods.TryGetModsData(playerInfo.UserId, out mods))
                        return false;
                    bool found = false;
                    foreach (var mod in mods)
                    {
                        if (mod.Id == MultiplayerStabilityMain.UniqueName)
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                        return false;
                }
                return true;
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.Log("[Transfer][ERR] peer mod check failed, staying vanilla: " + e);
                return false;
            }
        }

        private static void StartAckPumpLocked()
        {
            var photon = PhotonManager.Instance;
            s_PumpClient = (photon != null) ? ClientRef(photon) : null;
            if (s_PumpClient == null || s_AckPump != null)
                return;
            s_AckPump = new Timer(PumpAcks, null, AckPumpIntervalMs, AckPumpIntervalMs);
        }

        private static void StopAckPumpLocked()
        {
            s_AckPump?.Dispose();
            s_AckPump = null;
            s_PumpClient = null;
        }

        private static void PumpAcks(object _)
        {
            // Timer thread: an escaped exception would take down the process, so swallow everything.
            try
            {
                s_PumpClient?.LoadBalancingPeer?.SendAcksOnly();
            }
            catch (Exception)
            {
            }
        }
    }

    [HarmonyPatch(typeof(SaveNetManager), nameof(SaveNetManager.UploadSave))]
    internal static class SaveNetManager_UploadSave_Boost_Patch
    {
        private static void Prefix() => TransferBooster.OnTransferStarting("upload");
        private static void Finalizer(Exception __exception, Task __result)
            => TransferBooster.OnTransferReturned(__result, __exception, "upload");
    }

    [HarmonyPatch(typeof(SaveNetManager), nameof(SaveNetManager.DownloadSave))]
    internal static class SaveNetManager_DownloadSave_Boost_Patch
    {
        private static void Prefix() => TransferBooster.OnTransferStarting("download");
        private static void Finalizer(Exception __exception, Task __result)
            => TransferBooster.OnTransferReturned(__result, __exception, "download");
    }
}

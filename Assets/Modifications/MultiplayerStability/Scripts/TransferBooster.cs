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
//      Applied only after the compatibility decision confirms matching versions and compiled modules.
//      Without fast receiver acks the Photon server buffers the excess and can force-disconnect the receiver
//      (observed live: 6x192 KB burst -> DisconnectByDisconnectMessage).
using System;
using System.Reflection;
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
        private static readonly FieldInfo ClientField =
            AccessTools.Field(typeof(PhotonManager), "m_LoadBalancingClient");

        private static readonly object Sync = new object();
        private static Timer s_AckPump;
        private static LoadBalancingClient s_PumpClient;
        private static int s_ActiveTransfers;
        private static int s_Generation;
        private static int s_VanillaChunkBytes;
        private static int s_VanillaStreams;
        private static bool s_Boosted;
        private static SynchronizationContext s_MainContext;

        internal static int OnTransferStarting(string kind)
        {
            int generation = -1;
            try
            {
                lock (Sync)
                {
                    if (SynchronizationContext.Current != null)
                        s_MainContext = SynchronizationContext.Current;
                    generation = s_Generation;
                    s_ActiveTransfers++;
                    if (s_ActiveTransfers > 1)
                        return generation;

                    bool compatible = MultiplayerCompatibility.ProtocolsEnabled;
                    bool pumpActive = StartAckPumpLocked();
                    if (compatible && pumpActive && !s_Boosted)
                    {
                        s_VanillaChunkBytes = SaveMetaData.MaxPacketSize;
                        s_VanillaStreams = StreamsController.DefaultStreamsCount;
                        SaveMetaData.MaxPacketSize = BoostedChunkBytes;
                        StreamsController.DefaultStreamsCount = BoostedStreams;
                        s_Boosted = true;
                    }

                    MultiplayerStabilityMain.LogNoThrow(string.Format(
                        "[Transfer] {0} starting: chunk={1}KB streams={2} ackPump={3}ms/{4} exactBuild={5}",
                        kind, SaveMetaData.MaxPacketSize / 1024, StreamsController.DefaultStreamsCount,
                        AckPumpIntervalMs, pumpActive, compatible));
                }
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer][ERR] booster startup failed; transfer remains vanilla-compatible: "
                    + e.Message);
            }
            return generation;
        }

        // Called from a finalizer so a synchronous throw in UploadSave/DownloadSave (e.g.
        // AlreadyInProgressException) cannot leak the transfer count or the pump.
        internal static void OnTransferReturned(
            int generation, Task transferTask, Exception exception, string kind)
        {
            if (exception != null || transferTask == null)
            {
                OnTransferEnded(generation, kind + " (threw)");
                return;
            }
            try
            {
                transferTask.ContinueWith(
                    t => EndOnMainThread(
                        generation,
                        kind + (t.IsFaulted
                            ? " (faulted)"
                            : t.IsCanceled ? " (canceled)" : " (completed)")),
                    TaskScheduler.Default);
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer][ERR] completion continuation failed: " + e.Message);
                EndOnMainThread(generation, kind + " (continuation failed)");
            }
        }

        private static void EndOnMainThread(int generation, string kind)
        {
            SynchronizationContext context = s_MainContext;
            if (context == null || ReferenceEquals(context, SynchronizationContext.Current))
            {
                OnTransferEnded(generation, kind);
                return;
            }

            try
            {
                context.Post(_ => OnTransferEnded(generation, kind), null);
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer][ERR] main-thread restore dispatch failed: " + e.Message);
                OnTransferEnded(generation, kind);
            }
        }

        private static void OnTransferEnded(int generation, string kind)
        {
            lock (Sync)
            {
                // A room reset advances the generation. Late continuations from the old room must not
                // decrement a transfer that started after the reset.
                if (generation != s_Generation)
                    return;
                if (s_ActiveTransfers > 0)
                    s_ActiveTransfers--;
                if (s_ActiveTransfers > 0)
                    return;

                RestoreVanillaValuesLocked();
                StopAckPumpLocked();
                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer] " + kind + "; vanilla values restored, ack pump stopped.");
            }
        }

        internal static void ResetSession(string reason)
        {
            lock (Sync)
            {
                s_Generation++;
                s_ActiveTransfers = 0;
                RestoreVanillaValuesLocked();
                StopAckPumpLocked();
                s_MainContext = SynchronizationContext.Current;
                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer] Booster state reset (" + reason + ").");
            }
        }

        private static void RestoreVanillaValuesLocked()
        {
            if (!s_Boosted)
                return;
            SaveMetaData.MaxPacketSize = s_VanillaChunkBytes;
            StreamsController.DefaultStreamsCount = s_VanillaStreams;
            s_Boosted = false;
        }

        private static bool StartAckPumpLocked()
        {
            var photon = PhotonManager.Instance;
            try
            {
                s_PumpClient = photon != null
                    ? ClientField?.GetValue(photon) as LoadBalancingClient
                    : null;
            }
            catch
            {
                s_PumpClient = null;
            }
            if (s_AckPump != null)
                return true;
            if (s_PumpClient == null)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer][WARN] ACK pump unavailable; Photon chunk/window boost remains disabled.");
                return false;
            }
            s_AckPump = new Timer(PumpAcks, null, AckPumpIntervalMs, AckPumpIntervalMs);
            return true;
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
        // Nullable keeps the skipped-prefix default distinct from generation zero when another
        // high-priority prefix suppresses the original method.
        private static void Prefix(out int? __state)
            => __state = TransferBooster.OnTransferStarting("upload");

        private static void Finalizer(int? __state, Exception __exception, Task __result)
        {
            if (__state.HasValue && __state.Value >= 0)
                TransferBooster.OnTransferReturned(
                    __state.Value, __result, __exception, "upload");
        }
    }

    [HarmonyPatch(typeof(SaveNetManager), nameof(SaveNetManager.DownloadSave))]
    internal static class SaveNetManager_DownloadSave_Boost_Patch
    {
        private static void Prefix(out int? __state)
            => __state = TransferBooster.OnTransferStarting("download");

        private static void Finalizer(int? __state, Exception __exception, Task __result)
        {
            if (__state.HasValue && __state.Value >= 0)
                TransferBooster.OnTransferReturned(
                    __state.Value, __result, __exception, "download");
        }
    }

    [HarmonyPatch(typeof(ModsNetManager), nameof(ModsNetManager.OnLeave))]
    internal static class ModsNetManager_OnLeave_TransferBoosterReset_Patch
    {
        private static void Postfix() => TransferBooster.ResetSession("room leave");
    }

    [HarmonyPatch(typeof(ModsNetManager), nameof(ModsNetManager.OnJoinedLobby))]
    internal static class ModsNetManager_OnJoinedLobby_TransferBoosterReset_Patch
    {
        private static void Prefix() => TransferBooster.ResetSession("joined lobby");
    }
}

// Routes co-op save bulk bytes over Steam networking instead of the Photon relay, which delivered
// approximately 230 KB/s per peer in the recorded tests. Photon remains the control plane:
// LoadSave/RequestSave/SaveMeta and the session state machine are unchanged. Only the save bytes use
// Steam, and only when both peers
// are Steam + have this mod. Anything else (GOG/EGS peer, no mod, Steam init failure, mid-transfer P2P
// failure) falls back to the vanilla Photon transfer.
//
// Patch points:
//   * DataTransporter.SendSave (host) -- prefix: replace the returned Task with our P2P send (or fall back).
//   * MessageNetManager.OnMessage (both) -- prefix claims free Photon code 100 for the mod handshake
//     (QUERY/PONG/COMPLETE); every other code runs vanilla.
//   * SaveNetManager.m_DownloadSaveTcs (receiver) -- DownloadSave awaits this TCS, so completing it
//     with the Steam-delivered bytes finishes the vanilla flow with no fake receiver and no ack to suppress.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Kingmaker.Networking;
using Kingmaker.Networking.Player;
using Steamworks;

namespace MultiplayerStability
{
    internal static class SteamSaveTransfer
    {
        private const byte ControlCode = 100;   // free Photon event code (verified: 42-199 unrouted)
        private const byte MsgQuery = 1, MsgPong = 2, MsgComplete = 3;
        private const byte FrameHeader = 1, FrameData = 2;
        private const int ChunkSize = 256 * 1024;         // < Steam 512 KiB message cap
        private const int QueryTimeoutMs = 3000;          // peer has mod but isn't Steam / silent -> fallback
        private const int CompleteTimeoutMs = 60000;      // true stall guard; healthy completes in ms

        private static readonly AccessTools.FieldRef<SaveNetManager, TaskCompletionSource<byte[]>> DownloadTcsRef =
            AccessTools.FieldRefAccess<SaveNetManager, TaskCompletionSource<byte[]>>("m_DownloadSaveTcs");

        // Host-side pending handshake/completion waiters, keyed by target actor number. Main-thread only.
        private static readonly Dictionary<int, TaskCompletionSource<ulong>> s_pongWaiters = new Dictionary<int, TaskCompletionSource<ulong>>();
        private static readonly Dictionary<int, TaskCompletionSource<bool>> s_completeWaiters = new Dictionary<int, TaskCompletionSource<bool>>();

        // Receiver-side active download. Main-thread only.
        private sealed class Recv
        {
            public int HostActor; public ulong HostSteam; public byte[] Buffer; public int Total; public int Received;
            public IProgress<DataTransferProgressInfo> Progress;
        }
        private static Recv s_recv;

        // SaveNetManager.m_Progress drives the client's loading-bar; private nested type, so reflect once
        // and use it through the public IProgress interface.
        private static readonly System.Reflection.FieldInfo ProgressField =
            AccessTools.Field(typeof(SaveNetManager), "m_Progress");

        // Set true while we re-invoke the original SendSave for fallback, so our prefix lets it through.
        private static bool s_reentry;

        internal static void Wire()
        {
            SteamP2P.MessageHandler = OnSteamData;
            SteamP2P.SessionFailed = OnSteamSessionFailed;
        }

        // ---- Host: replace SendSave with the P2P path (or fall back) -----------------------------------
        [HarmonyPatch(typeof(DataTransporter), nameof(DataTransporter.SendSave))]
        private static class DataTransporter_SendSave_P2P_Patch
        {
            private static bool Prefix(DataTransporter __instance, List<PhotonActorNumber> targetActors,
                ArraySegment<byte> saveBytes, CancellationToken cancellationToken,
                IProgress<DataTransferProgressInfo> progress, ref Task __result)
            {
                if (s_reentry)
                    return true;
                try
                {
                    if (!SteamP2P.Available || targetActors == null || targetActors.Count == 0)
                        return true;
                    if (!AllTargetsHaveMod(targetActors))
                        return true;
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.Log("[Transfer][ERR] P2P gate, staying vanilla: " + e);
                    return true;
                }
                MultiplayerStabilityMain.Log("[Transfer] Steam P2P path for " + targetActors.Count + " target(s), " + (saveBytes.Count / 1024) + "KB.");
                __result = HostFlow(__instance, targetActors, saveBytes, cancellationToken, progress);
                return false;
            }
        }

        private static async Task HostFlow(DataTransporter dt, List<PhotonActorNumber> targets,
            ArraySegment<byte> bytes, CancellationToken ct, IProgress<DataTransferProgressInfo> progress)
        {
            try
            {
                SteamP2P.EnsureInit();
                Dictionary<int, ulong> peers = await ResolvePeers(targets, ct);
                foreach (var kv in peers)
                    SteamP2P.AllowSessionFrom(kv.Value);
                foreach (var kv in peers)
                    await SendToPeer(kv.Key, kv.Value, bytes, progress, ct);
                MultiplayerStabilityMain.Log("[Transfer] P2P upload complete (" + (bytes.Count / 1024) + "KB).");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;   // honor cancellation exactly like vanilla SendSave
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.Log("[Transfer] P2P failed -> Photon fallback: " + e.Message);
            }

            // Fallback: run the original SendSave (guard re-entry so our prefix lets it through).
            s_reentry = true;
            Task vanilla;
            try { vanilla = dt.SendSave(targets, bytes, ct, progress); }
            finally { s_reentry = false; }
            await vanilla;
        }

        private static async Task<Dictionary<int, ulong>> ResolvePeers(List<PhotonActorNumber> targets, CancellationToken ct)
        {
            var waiters = new List<KeyValuePair<int, Task<ulong>>>();
            foreach (var t in targets)
            {
                int actor = t.ActorNumber;
                var tcs = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
                s_pongWaiters[actor] = tcs;
                SendControl(actor, MsgQuery, SteamP2P.LocalSteamId);
                waiters.Add(new KeyValuePair<int, Task<ulong>>(actor, tcs.Task));
            }
            try
            {
                var all = new List<Task>(waiters.Count);
                foreach (var w in waiters) all.Add(w.Value);
                await WithTimeout(Task.WhenAll(all), QueryTimeoutMs, ct);
            }
            finally
            {
                foreach (var w in waiters) s_pongWaiters.Remove(w.Key);
            }
            var map = new Dictionary<int, ulong>();
            foreach (var w in waiters) map[w.Key] = w.Value.Result;
            return map;
        }

        // Adaptive send rate. Valve's transport sends at a FIXED rate (clamp of SendRateMin; no upward
        // probing is implemented), so the mod finds path capacity itself: start at 1 MB/s, double while
        // delivery keeps up (>=80% of rate), halve on loss (<50%). Field tests: a fixed 4 MB/s floor on
        // this pair's path collapsed goodput to ~3% via retransmits; fixed low floors waste good links.
        private const int RateStart = 1024 * 1024;
        private const int RateFloor = 256 * 1024;
        private const int RateCap = 16 * 1024 * 1024;

        private static async Task SendToPeer(int actor, ulong steamId, ArraySegment<byte> bytes,
            IProgress<DataTransferProgressInfo> progress, CancellationToken ct)
        {
            var completeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            s_completeWaiters[actor] = completeTcs;
            try
            {
                var header = new byte[5];
                header[0] = FrameHeader;
                WriteInt32(header, 1, bytes.Count);
                if (SteamP2P.Send(steamId, header, header.Length) != EResult.k_EResultOK)
                    throw new Exception("header send failed");

                int rate = RateStart;
                SteamP2P.SetSendRateFloor(rate);
                long lastDelivered = 0;
                int lastTick = Environment.TickCount;
                int off = 0, sent = 0, chunkNo = 0;

                void RateTick()
                {
                    int now = Environment.TickCount;
                    double dt = (now - lastTick) / 1000.0;
                    if (dt < 1.8)
                        return;
                    if (!SteamP2P.TryGetSessionNumbers(steamId, out int ping, out _, out float wire, out long pend, out string via))
                        return;
                    long delivered = sent - pend;                     // bytes confirmed out of Steam's buffer
                    double goodput = (delivered - lastDelivered) / dt;
                    lastTick = now;
                    lastDelivered = delivered;
                    if (goodput >= rate * 0.8) rate = Math.Min(rate * 2, RateCap);
                    else if (goodput < rate * 0.5) rate = Math.Max(rate / 2, RateFloor);
                    SteamP2P.SetSendRateFloor(rate);
                    MultiplayerStabilityMain.Log("[Transfer] P2P " + (sent / 1024) + "/" + (bytes.Count / 1024)
                        + "KB queued; goodput=" + (int)(goodput / 1024) + "KB/s rateCtl=" + (rate / 1024)
                        + "KB/s ping=" + ping + "ms wire=" + (int)(wire / 1024f) + "KB/s pending=" + (pend / 1024)
                        + "KB via [" + via + "]");
                }

                while (off < bytes.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    RateTick();
                    int len = Math.Min(ChunkSize, bytes.Count - off);
                    var frame = new byte[len + 1];
                    frame[0] = FrameData;
                    Buffer.BlockCopy(bytes.Array, bytes.Offset + off, frame, 1, len);

                    EResult res;
                    int guard = 0;
                    while ((res = SteamP2P.Send(steamId, frame, frame.Length)) == EResult.k_EResultLimitExceeded)
                    {
                        ct.ThrowIfCancellationRequested();
                        RateTick();
                        await Task.Delay(4, ct);                       // backpressure: Steam send buffer full
                        if (++guard > 15000) throw new Exception("send backpressure stuck");
                    }
                    if (res != EResult.k_EResultOK)
                        throw new Exception("data send failed: " + res);

                    off += len; sent += len;
                    progress?.Report(new DataTransferProgressInfo(len, sent, bytes.Count));
                    if ((++chunkNo & 7) == 0) await Task.Yield();      // keep the frame responsive
                }

                // Tail: everything is queued but up to SendBufferSize is still draining to the peer --
                // keep adapting the rate and reporting until the receiver confirms completion.
                int waitedMs = 0;
                while (true)
                {
                    var done = await Task.WhenAny(completeTcs.Task, Task.Delay(2000, ct));
                    if (done == completeTcs.Task)
                    {
                        await completeTcs.Task;
                        break;
                    }
                    RateTick();
                    waitedMs += 2000;
                    if (waitedMs >= CompleteTimeoutMs)
                        throw new TimeoutException("peer completion timeout");
                }
            }
            finally
            {
                s_completeWaiters.Remove(actor);
                SteamP2P.DisallowSessionFrom(steamId);
                SteamP2P.CloseSession(steamId);
            }
        }

        // ---- Both: mod handshake over Photon code 100 --------------------------------------------------
        [HarmonyPatch(typeof(MessageNetManager), nameof(MessageNetManager.OnMessage))]
        private static class MessageNetManager_OnMessage_Control_Patch
        {
            private static bool Prefix(byte code, int actorNumber, ReadOnlySpan<byte> bytes)
            {
                if (code != ControlCode)
                    return true;
                try { OnControl(actorNumber, bytes.ToArray()); }
                catch (Exception e) { MultiplayerStabilityMain.Log("[Transfer][ERR] control msg: " + e); }
                return false;   // consumed
            }
        }

        private static void OnControl(int actor, byte[] data)
        {
            if (data.Length < 9)
                return;
            byte type = data[0];
            ulong steamId = BitConverter.ToUInt64(data, 1);
            switch (type)
            {
                case MsgQuery:
                    if (SteamP2P.Available)
                    {
                        SteamP2P.EnsureInit();
                        s_recv = new Recv { HostActor = actor, HostSteam = steamId };
                        SteamP2P.AllowSessionFrom(steamId);
                        SendControl(actor, MsgPong, SteamP2P.LocalSteamId);
                        MultiplayerStabilityMain.Log("[Transfer] Armed P2P receive from actor " + actor + " (steam " + steamId + ").");
                    }
                    // else: not Steam -> stay silent; the host times out and falls back to Photon.
                    break;
                case MsgPong:
                    if (s_pongWaiters.TryGetValue(actor, out var pw)) pw.TrySetResult(steamId);
                    break;
                case MsgComplete:
                    if (s_completeWaiters.TryGetValue(actor, out var cw)) cw.TrySetResult(true);
                    break;
            }
        }

        // ---- Receiver: reassemble Steam bytes and complete the vanilla download ------------------------
        private static void OnSteamData(ulong from, byte[] msg)
        {
            var recv = s_recv;
            if (recv == null || from != recv.HostSteam || msg.Length < 1)
                return;
            switch (msg[0])
            {
                case FrameHeader:
                    if (msg.Length < 5) return;
                    recv.Total = BitConverter.ToInt32(msg, 1);
                    recv.Buffer = new byte[recv.Total];
                    recv.Received = 0;
                    try { recv.Progress = ProgressField?.GetValue(PhotonManager.Save) as IProgress<DataTransferProgressInfo>; }
                    catch (Exception) { recv.Progress = null; }
                    break;
                case FrameData:
                    if (recv.Buffer == null) return;
                    int len = msg.Length - 1;
                    if (recv.Received + len > recv.Total) return;   // guard stale/overflow
                    Buffer.BlockCopy(msg, 1, recv.Buffer, recv.Received, len);
                    recv.Received += len;
                    recv.Progress?.Report(new DataTransferProgressInfo(len, recv.Received, recv.Total));
                    if (recv.Received >= recv.Total)
                        CompleteReceive(recv);
                    break;
            }
        }

        private static void CompleteReceive(Recv recv)
        {
            s_recv = null;
            var save = PhotonManager.Save;
            var tcs = (save != null) ? DownloadTcsRef(save) : null;
            if (tcs == null)
            {
                MultiplayerStabilityMain.Log("[Transfer][ERR] no download TCS; letting Photon fallback deliver.");
            }
            else if (!save.InProcess)
            {
                MultiplayerStabilityMain.Log("[Transfer][ERR] save not InProcess; discarding P2P bytes.");
            }
            else
            {
                bool set = tcs.TrySetResult(recv.Buffer);
                MultiplayerStabilityMain.Log("[Transfer] P2P download complete (" + (recv.Total / 1024) + "KB), fed to game=" + set + ".");
            }
            SendControl(recv.HostActor, MsgComplete, SteamP2P.LocalSteamId);
            SteamP2P.DisallowSessionFrom(recv.HostSteam);
            SteamP2P.CloseSession(recv.HostSteam);
        }

        private static void OnSteamSessionFailed(ulong steamId)
        {
            // A dropped Steam session means P2P is dead for this transfer: fault every pending waiter so
            // the host falls back to Photon, and drop any half-built receive so Photon fallback can deliver.
            var ex = new Exception("steam session failed");
            foreach (var w in s_pongWaiters.Values) w.TrySetException(ex);
            foreach (var c in s_completeWaiters.Values) c.TrySetException(ex);
            if (s_recv != null && s_recv.HostSteam == steamId) s_recv = null;
        }

        // ---- helpers -----------------------------------------------------------------------------------
        private static bool AllTargetsHaveMod(List<PhotonActorNumber> targets)
        {
            var photon = PhotonManager.Instance;
            if (photon == null)
                return false;
            foreach (var t in targets)
            {
                string userId = null;
                foreach (PlayerInfo pi in photon.AllPlayers)
                    if (pi.Player.ActorNumber == t.ActorNumber) { userId = pi.UserId; break; }
                if (userId == null || !PhotonManager.Mods.TryGetModsData(userId, out var mods) || mods == null)
                    return false;
                bool has = false;
                foreach (var m in mods)
                    if (m.Id == MultiplayerStabilityMain.UniqueName) { has = true; break; }
                if (!has)
                    return false;
            }
            return true;
        }

        private static void SendControl(int actor, byte type, ulong steamId)
        {
            var buf = new byte[9];
            buf[0] = type;
            byte[] idb = BitConverter.GetBytes(steamId);
            Buffer.BlockCopy(idb, 0, buf, 1, 8);
            PhotonManager.Instance?.SendMessageTo(new PhotonActorNumber(actor), ControlCode, buf, 0, buf.Length);
        }

        private static void WriteInt32(byte[] buf, int offset, int value)
        {
            byte[] b = BitConverter.GetBytes(value);
            Buffer.BlockCopy(b, 0, buf, offset, 4);
        }

        private static async Task WithTimeout(Task task, int ms, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var finished = await Task.WhenAny(task, Task.Delay(ms, cts.Token));
                if (finished != task)
                    throw new TimeoutException("P2P step timed out after " + ms + "ms");
                cts.Cancel();
                await task;   // observe result / propagate exception
            }
        }
    }
}

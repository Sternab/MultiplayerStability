// Optional bulk-save transport over SteamNetworkingMessages.
//
// Photon remains the control plane. LoadSave, RequestSave, SaveMeta, settings, portraits, and the
// NetGame state machine are unchanged. Only the packed save byte array may use Steam, and only when
// the session-latched compatibility gate confirms the exact same build on every peer.
//
// Protocol contract:
//   * Photon code 100 is claimed only when the payload starts with this protocol's magic and version.
//   * Every control and data frame carries a transfer ID, so stale packets cannot satisfy a new wait.
//   * The receiver enforces a 512 MiB bound, exact ordered offsets, declared length, and SHA-256.
//   * COMPLETE is sent only after SaveNetManager's current download TCS accepts the verified bytes.
//     Missing/stale game state, checksum errors, and rejected completion send NACK and use Photon.
//   * Multi-peer uploads fall back only for peers that have not acknowledged completion.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Kingmaker.Networking;
using Steamworks;

namespace MultiplayerStability
{
    internal static class SteamSaveTransfer
    {
        private const byte ControlCode = 100;
        private const uint Magic = 0x5453504D; // "MPST" in little-endian byte order
        private const byte ProtocolVersion = 1;

        private const byte MsgQuery = 1;
        private const byte MsgPong = 2;
        private const byte MsgComplete = 3;
        private const byte MsgNack = 4;
        private const byte MsgCancel = 5;

        private const byte FrameHeader = 1;
        private const byte FrameData = 2;

        private const int ControlFrameSize = 23;
        private const int HeaderFrameSize = 50;
        private const int DataFramePrefixSize = 18;
        private const int ChunkSize = 256 * 1024;
        private const int MaxSaveBytes = 512 * 1024 * 1024;
        private const int QueryTimeoutMs = 3000;
        private const int CompleteTimeoutMs = 60000;
        private const int ReceiveIdleTimeoutMs = 90000;

        private const int RateStart = 1024 * 1024;
        private const int RateFloor = 256 * 1024;
        private const int RateCap = 16 * 1024 * 1024;

        private enum NackReason : byte
        {
            None,
            Incompatible,
            Busy,
            InvalidFrame,
            TooLarge,
            Checksum,
            GameRejected,
            TransportFailed
        }

        private readonly struct TransferKey : IEquatable<TransferKey>
        {
            internal readonly int Actor;
            internal readonly ulong TransferId;

            internal TransferKey(int actor, ulong transferId)
            {
                Actor = actor;
                TransferId = transferId;
            }

            public bool Equals(TransferKey other)
                => Actor == other.Actor && TransferId == other.TransferId;

            public override bool Equals(object obj)
                => obj is TransferKey other && Equals(other);

            public override int GetHashCode()
                => (Actor * 397) ^ TransferId.GetHashCode();
        }

        private sealed class PeerTransfer
        {
            internal int Actor;
            internal ulong TransferId;
            internal ulong SteamId;
        }

        private sealed class Recv
        {
            internal int HostActor;
            internal ulong HostSteam;
            internal ulong TransferId;
            internal byte[] Buffer;
            internal byte[] ExpectedHash;
            internal int Total;
            internal int Received;
            internal DateTime LastActivityUtc;
            internal IProgress<DataTransferProgressInfo> Progress;
        }

        private static readonly Dictionary<TransferKey, TaskCompletionSource<ulong>> s_pongWaiters =
            new Dictionary<TransferKey, TaskCompletionSource<ulong>>();
        private static readonly Dictionary<TransferKey, TaskCompletionSource<bool>> s_completeWaiters =
            new Dictionary<TransferKey, TaskCompletionSource<bool>>();
        private static readonly Dictionary<TransferKey, ulong> s_completeSteamIds =
            new Dictionary<TransferKey, ulong>();

        private static readonly FieldInfo DownloadTcsField =
            AccessTools.Field(typeof(SaveNetManager), "m_DownloadSaveTcs");
        private static readonly FieldInfo ProgressField =
            AccessTools.Field(typeof(SaveNetManager), "m_Progress");

        private static Recv s_recv;
        private static bool s_reentry;
        private static long s_nextTransferId = DateTime.UtcNow.Ticks;

        internal static void Wire()
        {
            SteamP2P.MessageHandler = OnSteamData;
            SteamP2P.SessionFailed = OnSteamSessionFailed;
        }

        [HarmonyPatch(typeof(DataTransporter), nameof(DataTransporter.SendSave))]
        private static class DataTransporter_SendSave_P2P_Patch
        {
            private static bool Prefix(
                DataTransporter __instance,
                List<PhotonActorNumber> targetActors,
                ArraySegment<byte> saveBytes,
                CancellationToken cancellationToken,
                IProgress<DataTransferProgressInfo> progress,
                ref Task __result)
            {
                if (s_reentry)
                    return true;

                try
                {
                    if (!MultiplayerCompatibility.ProtocolsEnabled
                        || !SteamP2P.Available
                        || targetActors == null
                        || targetActors.Count == 0
                        || saveBytes.Array == null
                        || saveBytes.Count <= 0
                        || saveBytes.Count > MaxSaveBytes)
                    {
                        return true;
                    }
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[Transfer][ERR] P2P gate failed; using Photon: " + e.Message);
                    return true;
                }

                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer] Steam path offered to " + targetActors.Count + " peer(s), "
                    + (saveBytes.Count / 1024) + "KB.");
                __result = HostFlow(__instance, targetActors, saveBytes, cancellationToken, progress);
                return false;
            }
        }

        private static async Task HostFlow(
            DataTransporter transporter,
            List<PhotonActorNumber> targets,
            ArraySegment<byte> bytes,
            CancellationToken cancellationToken,
            IProgress<DataTransferProgressInfo> progress)
        {
            var pending = new List<PhotonActorNumber>(targets);
            try
            {
                SteamP2P.EnsureInit();
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer] Steam initialization failed; using Photon: " + e.Message);
                await SendPhotonFallback(
                    transporter, pending, bytes, cancellationToken, progress);
                return;
            }

            for (int i = 0; i < targets.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PhotonActorNumber target = targets[i];
                PeerTransfer peer = null;
                try
                {
                    peer = await ResolvePeer(target.ActorNumber, cancellationToken);
                    await SendToPeer(peer, bytes, progress, cancellationToken);
                    RemoveActor(pending, target.ActorNumber);
                    MultiplayerStabilityMain.LogNoThrow(
                        "[Transfer] Actor " + target.ActorNumber + " accepted verified Steam bytes.");
                }
                catch (OperationCanceledException)
                {
                    if (peer != null)
                        CancelPeer(peer, NackReason.TransportFailed);
                    throw;
                }
                catch (Exception e)
                {
                    if (peer != null)
                        CancelPeer(peer, NackReason.TransportFailed);
                    MultiplayerStabilityMain.LogNoThrow(
                        "[Transfer] Actor " + target.ActorNumber + " will use Photon: " + e.Message);
                }
            }

            if (pending.Count == 0)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer] Steam upload complete (" + (bytes.Count / 1024) + "KB).");
                return;
            }

            await SendPhotonFallback(transporter, pending, bytes, cancellationToken, progress);
        }

        private static async Task SendPhotonFallback(
            DataTransporter transporter,
            List<PhotonActorNumber> pending,
            ArraySegment<byte> bytes,
            CancellationToken cancellationToken,
            IProgress<DataTransferProgressInfo> progress)
        {
            MultiplayerStabilityMain.LogNoThrow(
                "[Transfer] Photon fallback for " + pending.Count + " unfinished peer(s).");
            s_reentry = true;
            Task vanilla;
            try
            {
                vanilla = transporter.SendSave(pending, bytes, cancellationToken, progress);
            }
            finally
            {
                s_reentry = false;
            }
            await vanilla;
        }

        private static async Task<PeerTransfer> ResolvePeer(int actor, CancellationToken cancellationToken)
        {
            ulong transferId = unchecked((ulong)Interlocked.Increment(ref s_nextTransferId));
            var key = new TransferKey(actor, transferId);
            var waiter = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
            s_pongWaiters[key] = waiter;
            bool querySent = false;
            try
            {
                if (!SendControl(actor, MsgQuery, transferId, SteamP2P.LocalSteamId, NackReason.None))
                    throw new Exception("query send failed");
                querySent = true;
                await WithTimeout(waiter.Task, QueryTimeoutMs, cancellationToken);
                ulong steamId = await waiter.Task;
                return new PeerTransfer
                {
                    Actor = actor,
                    TransferId = transferId,
                    SteamId = steamId
                };
            }
            catch
            {
                if (querySent)
                {
                    SendControl(
                        actor,
                        MsgCancel,
                        transferId,
                        SteamP2P.LocalSteamId,
                        NackReason.TransportFailed);
                }
                throw;
            }
            finally
            {
                s_pongWaiters.Remove(key);
            }
        }

        private static async Task SendToPeer(
            PeerTransfer peer,
            ArraySegment<byte> bytes,
            IProgress<DataTransferProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            var key = new TransferKey(peer.Actor, peer.TransferId);
            var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            s_completeWaiters[key] = complete;
            s_completeSteamIds[key] = peer.SteamId;
            SteamP2P.AllowSessionFrom(peer.SteamId);
            try
            {
                byte[] digest;
                using (var sha = SHA256.Create())
                    digest = sha.ComputeHash(bytes.Array, bytes.Offset, bytes.Count);

                var header = new byte[HeaderFrameSize];
                WriteUInt32(header, 0, Magic);
                header[4] = ProtocolVersion;
                header[5] = FrameHeader;
                WriteUInt64(header, 6, peer.TransferId);
                WriteInt32(header, 14, bytes.Count);
                Buffer.BlockCopy(digest, 0, header, 18, digest.Length);
                EnsureSent(peer.SteamId, header, "header");

                int rate = RateStart;
                SteamP2P.SetSendRateFloor(rate);
                long lastDelivered = 0;
                int lastTick = Environment.TickCount;
                int offset = 0;
                int sent = 0;
                int chunkNo = 0;

                void RateTick()
                {
                    int now = Environment.TickCount;
                    double elapsed = unchecked(now - lastTick) / 1000.0;
                    if (elapsed < 1.8
                        || !SteamP2P.TryGetSessionNumbers(
                            peer.SteamId, out int ping, out _, out float wire, out long pending, out string via))
                    {
                        return;
                    }

                    long delivered = sent - pending;
                    double goodput = (delivered - lastDelivered) / elapsed;
                    lastTick = now;
                    lastDelivered = delivered;
                    if (goodput >= rate * 0.8)
                        rate = Math.Min(rate * 2, RateCap);
                    else if (goodput < rate * 0.5)
                        rate = Math.Max(rate / 2, RateFloor);
                    SteamP2P.SetSendRateFloor(rate);
                    MultiplayerStabilityMain.LogNoThrow(
                        "[Transfer] actor=" + peer.Actor + " " + (sent / 1024) + "/"
                        + (bytes.Count / 1024) + "KB queued; goodput=" + (int)(goodput / 1024)
                        + "KB/s rateCtl=" + (rate / 1024) + "KB/s ping=" + ping
                        + "ms wire=" + (int)(wire / 1024f) + "KB/s pending=" + (pending / 1024)
                        + "KB via [" + via + "]");
                }

                while (offset < bytes.Count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RateTick();
                    int payloadLength = Math.Min(ChunkSize, bytes.Count - offset);
                    var frame = new byte[DataFramePrefixSize + payloadLength];
                    WriteUInt32(frame, 0, Magic);
                    frame[4] = ProtocolVersion;
                    frame[5] = FrameData;
                    WriteUInt64(frame, 6, peer.TransferId);
                    WriteInt32(frame, 14, offset);
                    Buffer.BlockCopy(
                        bytes.Array, bytes.Offset + offset, frame, DataFramePrefixSize, payloadLength);

                    EResult result;
                    int backpressureIterations = 0;
                    while ((result = SteamP2P.Send(peer.SteamId, frame, frame.Length))
                        == EResult.k_EResultLimitExceeded)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        RateTick();
                        await Task.Delay(4, cancellationToken);
                        if (++backpressureIterations > 15000)
                            throw new Exception("send backpressure did not clear");
                    }
                    if (result != EResult.k_EResultOK)
                        throw new Exception("data send failed: " + result);

                    offset += payloadLength;
                    sent += payloadLength;
                    ReportProgressNoThrow(progress, payloadLength, sent, bytes.Count);
                    if ((++chunkNo & 7) == 0)
                        await Task.Yield();
                }

                int waitedMs = 0;
                while (true)
                {
                    Task finished = await Task.WhenAny(complete.Task, Task.Delay(2000, cancellationToken));
                    if (finished == complete.Task)
                    {
                        await complete.Task;
                        break;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    RateTick();
                    waitedMs += 2000;
                    if (waitedMs >= CompleteTimeoutMs)
                        throw new TimeoutException("peer completion timeout");
                }
            }
            finally
            {
                s_completeWaiters.Remove(key);
                s_completeSteamIds.Remove(key);
                SteamP2P.DisallowSessionFrom(peer.SteamId);
                SteamP2P.CloseSession(peer.SteamId);
            }
        }

        [HarmonyPatch(typeof(MessageNetManager), nameof(MessageNetManager.OnMessage))]
        private static class MessageNetManager_OnMessage_Control_Patch
        {
            private static bool Prefix(byte code, int actorNumber, ReadOnlySpan<byte> bytes)
            {
                if (code != ControlCode || !HasMagic(bytes))
                    return true;

                try
                {
                    OnControl(actorNumber, bytes);
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[Transfer][ERR] rejected control frame: " + e.Message);
                }
                return false;
            }
        }

        private static void OnControl(int actor, ReadOnlySpan<byte> data)
        {
            if (data.Length != ControlFrameSize)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer][ERR] control frame length " + data.Length + " (expected "
                    + ControlFrameSize + ").");
                return;
            }
            if (data[4] != ProtocolVersion)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer][ERR] unsupported protocol version " + data[4] + ".");
                return;
            }

            byte type = data[5];
            ulong transferId = ReadUInt64(data, 6);
            ulong steamId = ReadUInt64(data, 14);
            var reason = (NackReason)data[22];
            var key = new TransferKey(actor, transferId);

            switch (type)
            {
                case MsgQuery:
                    HandleQuery(actor, transferId, steamId);
                    break;
                case MsgPong:
                    if (s_pongWaiters.TryGetValue(key, out TaskCompletionSource<ulong> pong))
                    {
                        if (steamId == 0UL)
                            pong.TrySetException(new Exception("peer returned an invalid Steam ID"));
                        else
                            pong.TrySetResult(steamId);
                    }
                    break;
                case MsgComplete:
                    if (s_completeWaiters.TryGetValue(key, out TaskCompletionSource<bool> complete))
                        complete.TrySetResult(true);
                    break;
                case MsgNack:
                    var error = new Exception("peer NACK: " + reason);
                    if (s_pongWaiters.TryGetValue(key, out TaskCompletionSource<ulong> query))
                        query.TrySetException(error);
                    if (s_completeWaiters.TryGetValue(key, out TaskCompletionSource<bool> ack))
                        ack.TrySetException(error);
                    break;
                case MsgCancel:
                    if (s_recv != null
                        && s_recv.HostActor == actor
                        && s_recv.TransferId == transferId)
                    {
                        CloseReceive(s_recv);
                        MultiplayerStabilityMain.LogNoThrow(
                            "[Transfer] Receive cancelled for actor " + actor + ".");
                    }
                    break;
                default:
                    MultiplayerStabilityMain.LogNoThrow(
                        "[Transfer][ERR] unknown control message " + type + ".");
                    break;
            }
        }

        private static void HandleQuery(int actor, ulong transferId, ulong hostSteam)
        {
            if (!MultiplayerCompatibility.ProtocolsEnabled)
            {
                SendControl(actor, MsgNack, transferId, SteamP2P.LocalSteamId, NackReason.Incompatible);
                return;
            }
            if (!SteamP2P.Available)
            {
                SendControl(actor, MsgNack, transferId, 0UL, NackReason.TransportFailed);
                return;
            }
            if (hostSteam == 0UL)
            {
                SendControl(actor, MsgNack, transferId, SteamP2P.LocalSteamId, NackReason.InvalidFrame);
                return;
            }

            SteamP2P.EnsureInit();
            if (s_recv != null
                && DateTime.UtcNow - s_recv.LastActivityUtc
                    >= TimeSpan.FromMilliseconds(ReceiveIdleTimeoutMs))
            {
                Recv expired = s_recv;
                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer][WARN] Discarding idle receive " + expired.TransferId
                    + " before accepting a new query.");
                CloseReceive(expired);
            }
            if (s_recv != null)
            {
                if (s_recv.HostActor == actor && s_recv.TransferId == transferId)
                {
                    s_recv.LastActivityUtc = DateTime.UtcNow;
                    SendControl(actor, MsgPong, transferId, SteamP2P.LocalSteamId, NackReason.None);
                    return;
                }
                SendControl(actor, MsgNack, transferId, SteamP2P.LocalSteamId, NackReason.Busy);
                return;
            }

            s_recv = new Recv
            {
                HostActor = actor,
                HostSteam = hostSteam,
                TransferId = transferId,
                LastActivityUtc = DateTime.UtcNow
            };
            SteamP2P.AllowSessionFrom(hostSteam);
            if (!SendControl(actor, MsgPong, transferId, SteamP2P.LocalSteamId, NackReason.None))
            {
                CloseReceive(s_recv);
                return;
            }
            MultiplayerStabilityMain.LogNoThrow(
                "[Transfer] Armed receive actor=" + actor + " transfer=" + transferId + ".");
        }

        private static void OnSteamData(ulong from, byte[] message)
        {
            try
            {
                Recv recv = s_recv;
                if (recv == null
                    || from != recv.HostSteam
                    || message == null
                    || message.Length < DataFramePrefixSize
                    || ReadUInt32(message, 0) != Magic
                    || message[4] != ProtocolVersion
                    || ReadUInt64(message, 6) != recv.TransferId)
                {
                    return;
                }

                switch (message[5])
                {
                    case FrameHeader:
                        recv.LastActivityUtc = DateTime.UtcNow;
                        ReceiveHeader(recv, message);
                        break;
                    case FrameData:
                        recv.LastActivityUtc = DateTime.UtcNow;
                        ReceiveData(recv, message);
                        break;
                    default:
                        RejectReceive(recv, NackReason.InvalidFrame, "unknown Steam frame type");
                        break;
                }
            }
            catch (Exception e)
            {
                Recv recv = s_recv;
                if (recv != null)
                    RejectReceive(recv, NackReason.InvalidFrame, "receiver exception: " + e.Message);
            }
        }

        private static void ReceiveHeader(Recv recv, byte[] message)
        {
            if (message.Length != HeaderFrameSize || recv.Buffer != null)
            {
                RejectReceive(recv, NackReason.InvalidFrame, "invalid or duplicate header");
                return;
            }

            int total = ReadInt32(message, 14);
            if (total <= 0 || total > MaxSaveBytes)
            {
                RejectReceive(recv, NackReason.TooLarge, "declared size " + total);
                return;
            }

            recv.Total = total;
            recv.Buffer = new byte[total];
            recv.ExpectedHash = new byte[32];
            Buffer.BlockCopy(message, 18, recv.ExpectedHash, 0, recv.ExpectedHash.Length);
            recv.Received = 0;
            try
            {
                recv.Progress = ProgressField?.GetValue(PhotonManager.Save)
                    as IProgress<DataTransferProgressInfo>;
            }
            catch
            {
                recv.Progress = null;
            }
        }

        private static void ReceiveData(Recv recv, byte[] message)
        {
            if (recv.Buffer == null || message.Length <= DataFramePrefixSize)
            {
                RejectReceive(recv, NackReason.InvalidFrame, "data arrived before header");
                return;
            }

            int offset = ReadInt32(message, 14);
            int payloadLength = message.Length - DataFramePrefixSize;
            if (offset != recv.Received
                || payloadLength > ChunkSize
                || payloadLength > recv.Total - recv.Received)
            {
                RejectReceive(
                    recv,
                    NackReason.InvalidFrame,
                    "offset " + offset + ", expected " + recv.Received + ", payload " + payloadLength);
                return;
            }

            Buffer.BlockCopy(message, DataFramePrefixSize, recv.Buffer, offset, payloadLength);
            recv.Received += payloadLength;
            ReportProgressNoThrow(recv.Progress, payloadLength, recv.Received, recv.Total);
            if (recv.Received == recv.Total)
                CompleteReceive(recv);
        }

        private static void CompleteReceive(Recv recv)
        {
            byte[] actual;
            using (var sha = SHA256.Create())
                actual = sha.ComputeHash(recv.Buffer);
            if (!FixedTimeEquals(actual, recv.ExpectedHash))
            {
                RejectReceive(recv, NackReason.Checksum, "SHA-256 mismatch");
                return;
            }

            bool accepted = false;
            string failure = null;
            try
            {
                SaveNetManager save = PhotonManager.Save;
                var tcs = save != null
                    ? DownloadTcsField?.GetValue(save) as TaskCompletionSource<byte[]>
                    : null;
                if (save == null)
                    failure = "SaveNetManager unavailable";
                else if (!save.InProcess)
                    failure = "save is not in process";
                else if (tcs == null)
                    failure = "download TCS unavailable";
                else
                    accepted = tcs.TrySetResult(recv.Buffer);
                if (!accepted && failure == null)
                    failure = "download TCS rejected completion";
            }
            catch (Exception e)
            {
                failure = "game completion failed: " + e.Message;
            }

            if (accepted)
            {
                // The game has accepted the bytes and cannot be rolled back. Repeat the small reliable
                // completion frame so a transient control-send failure is unlikely to make the host time out
                // and resend the same save over Photon. Duplicate completion frames are idempotent.
                bool acknowledgementSent = false;
                for (int i = 0; i < 3; i++)
                {
                    acknowledgementSent |= SendControl(
                        recv.HostActor,
                        MsgComplete,
                        recv.TransferId,
                        SteamP2P.LocalSteamId,
                        NackReason.None);
                }
                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer] Verified download accepted by game (" + (recv.Total / 1024)
                    + "KB); completion acknowledgement sent=" + acknowledgementSent + ".");
                CloseReceive(recv);
                return;
            }

            RejectReceive(recv, NackReason.GameRejected, failure);
        }

        private static void RejectReceive(Recv recv, NackReason reason, string detail)
        {
            SendControl(
                recv.HostActor,
                MsgNack,
                recv.TransferId,
                SteamP2P.LocalSteamId,
                reason);
            MultiplayerStabilityMain.LogNoThrow(
                "[Transfer][ERR] Rejected Steam transfer " + recv.TransferId + ": " + detail
                + "; host will use Photon.");
            CloseReceive(recv);
        }

        private static void CloseReceive(Recv recv)
        {
            if (recv == null)
                return;
            if (ReferenceEquals(s_recv, recv))
                s_recv = null;
            SteamP2P.DisallowSessionFrom(recv.HostSteam);
            SteamP2P.CloseSession(recv.HostSteam);
        }

        private static void CancelPeer(PeerTransfer peer, NackReason reason)
        {
            SendControl(
                peer.Actor,
                MsgCancel,
                peer.TransferId,
                SteamP2P.LocalSteamId,
                reason);
            SteamP2P.DisallowSessionFrom(peer.SteamId);
            SteamP2P.CloseSession(peer.SteamId);
        }

        private static void OnSteamSessionFailed(ulong steamId)
        {
            var error = new Exception("Steam session failed");
            var affected = new List<TransferKey>();
            foreach (var pair in s_completeSteamIds)
            {
                if (pair.Value == steamId)
                    affected.Add(pair.Key);
            }
            for (int i = 0; i < affected.Count; i++)
            {
                if (s_completeWaiters.TryGetValue(
                    affected[i], out TaskCompletionSource<bool> waiter))
                {
                    waiter.TrySetException(error);
                }
            }

            if (s_recv != null && s_recv.HostSteam == steamId)
            {
                Recv recv = s_recv;
                SendControl(
                    recv.HostActor,
                    MsgNack,
                    recv.TransferId,
                    SteamP2P.LocalSteamId,
                    NackReason.TransportFailed);
                CloseReceive(recv);
            }
        }

        internal static void ResetSession(string reason)
        {
            var error = new OperationCanceledException("Steam transfer reset: " + reason);
            var outgoingSteamIds = new HashSet<ulong>(s_completeSteamIds.Values);
            foreach (TaskCompletionSource<ulong> waiter in s_pongWaiters.Values)
                waiter.TrySetException(error);
            foreach (TaskCompletionSource<bool> waiter in s_completeWaiters.Values)
                waiter.TrySetException(error);
            s_pongWaiters.Clear();
            s_completeWaiters.Clear();
            s_completeSteamIds.Clear();
            foreach (ulong steamId in outgoingSteamIds)
            {
                SteamP2P.DisallowSessionFrom(steamId);
                SteamP2P.CloseSession(steamId);
            }

            Recv recv = s_recv;
            if (recv != null)
                CloseReceive(recv);
            MultiplayerStabilityMain.LogNoThrow("[Transfer] Session state reset (" + reason + ").");
        }

        private static bool SendControl(
            int actor,
            byte type,
            ulong transferId,
            ulong steamId,
            NackReason reason)
        {
            var frame = new byte[ControlFrameSize];
            WriteUInt32(frame, 0, Magic);
            frame[4] = ProtocolVersion;
            frame[5] = type;
            WriteUInt64(frame, 6, transferId);
            WriteUInt64(frame, 14, steamId);
            frame[22] = (byte)reason;
            try
            {
                return PhotonManager.Instance != null
                    && PhotonManager.Instance.SendMessageTo(
                        new PhotonActorNumber(actor), ControlCode, frame, 0, frame.Length);
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[Transfer][ERR] control send actor=" + actor + " type=" + type + ": " + e.Message);
                return false;
            }
        }

        private static void EnsureSent(ulong steamId, byte[] frame, string kind)
        {
            EResult result = SteamP2P.Send(steamId, frame, frame.Length);
            if (result != EResult.k_EResultOK)
                throw new Exception(kind + " send failed: " + result);
        }

        private static void RemoveActor(List<PhotonActorNumber> actors, int actor)
        {
            for (int i = actors.Count - 1; i >= 0; i--)
            {
                if (actors[i].ActorNumber == actor)
                {
                    actors.RemoveAt(i);
                    return;
                }
            }
        }

        private static void ReportProgressNoThrow(
            IProgress<DataTransferProgressInfo> progress,
            int delta,
            int current,
            int total)
        {
            try
            {
                progress?.Report(new DataTransferProgressInfo(delta, current, total));
            }
            catch
            {
                // UI progress is not part of the transfer acceptance contract.
            }
        }

        private static bool HasMagic(ReadOnlySpan<byte> bytes)
            => bytes.Length >= 4 && ReadUInt32(bytes, 0) == Magic;

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            int diff = 0;
            for (int i = 0; i < left.Length; i++)
                diff |= left[i] ^ right[i];
            return diff == 0;
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
            => WriteUInt32(buffer, offset, unchecked((uint)value));

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
                buffer[offset + i] = (byte)(value >> (i * 8));
        }

        private static int ReadInt32(byte[] buffer, int offset)
            => unchecked((int)ReadUInt32(buffer, offset));

        private static uint ReadUInt32(byte[] buffer, int offset)
            => (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));

        private static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset)
            => (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));

        private static ulong ReadUInt64(ReadOnlySpan<byte> buffer, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++)
                value |= (ulong)buffer[offset + i] << (i * 8);
            return value;
        }

        private static ulong ReadUInt64(byte[] buffer, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++)
                value |= (ulong)buffer[offset + i] << (i * 8);
            return value;
        }

        private static async Task WithTimeout(Task task, int milliseconds, CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task finished = await Task.WhenAny(task, Task.Delay(milliseconds, timeout.Token));
                if (finished != task)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("P2P step timed out after " + milliseconds + "ms");
                }
                timeout.Cancel();
                await task;
            }
        }
    }

    [HarmonyPatch(typeof(ModsNetManager), nameof(ModsNetManager.OnLeave))]
    internal static class ModsNetManager_OnLeave_SteamTransferReset_Patch
    {
        private static void Postfix() => SteamSaveTransfer.ResetSession("room leave");
    }

    [HarmonyPatch(typeof(ModsNetManager), nameof(ModsNetManager.OnJoinedLobby))]
    internal static class ModsNetManager_OnJoinedLobby_SteamTransferReset_Patch
    {
        private static void Prefix() => SteamSaveTransfer.ResetSession("joined lobby");
    }
}

// Sequenced co-op loading barriers.
//
// Rogue Trader reuses the same one-byte lock point for several barriers in an area transition. A fast
// peer can announce the next occurrence while a slow peer still owns the previous accumulator. This
// protocol adds an epoch-local ordinal so the announcements cannot be consumed by the wrong barrier.
//
// The protocol runs only under the session-latched exact-build gate. Reach messages are idempotent and
// retransmitted once per second. A malformed/mixed packet, internal failure, or 30-second timeout sends
// an explicit epoch ABORT. Peers that receive it clear sequenced state and resume the untouched one-byte
// vanilla protocol. Delivery still depends on Photon's reliable message path; this is coordinated fallback,
// not a distributed-consensus guarantee.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.Networking;
using UnityEngine;

namespace MultiplayerStability
{
    internal static class SequencedLocks
    {
        private const uint Magic = 0x4B4C534D; // "MSLK" in little-endian byte order
        private const byte ProtocolVersion = 1;
        private const byte KindReach = 1;
        private const byte KindAbort = 2;
        private const int FrameSize = 11;
        private const float RetrySeconds = 1f;
        private const float AbortSeconds = 30f;

        private static readonly Dictionary<long, NetPlayerGroup> s_groups =
            new Dictionary<long, NetPlayerGroup>();
        private static readonly Dictionary<byte, int> s_nextSeq =
            new Dictionary<byte, int>();
        private static readonly Dictionary<byte, int> s_activeSeq =
            new Dictionary<byte, int>();
        private static readonly Dictionary<byte, float> s_activeSince =
            new Dictionary<byte, float>();
        private static readonly Dictionary<byte, float> s_lastAnnounce =
            new Dictionary<byte, float>();

        private static bool s_aborted;
        private static bool s_loggedActive;

        private static long Key(byte point, int sequence)
            => ((long)point << 32) | (uint)sequence;

        internal static bool TryLock(NetLockPointId pointId, out bool result)
        {
            result = false;
            if (!MultiplayerCompatibility.ProtocolsEnabled || s_aborted)
                return false;

            byte point = (byte)pointId;
            try
            {
                if (!s_activeSeq.TryGetValue(point, out int sequence))
                {
                    sequence = s_nextSeq.TryGetValue(point, out int next) ? next : 0;
                    s_nextSeq[point] = sequence + 1;
                    s_activeSeq[point] = sequence;
                    s_activeSince[point] = Time.realtimeSinceStartup;
                    s_lastAnnounce[point] = float.NegativeInfinity;
                    long initialKey = Key(point, sequence);
                    s_groups[initialKey] = Get(initialKey).Add(NetworkingManager.LocalNetPlayer);
                    LogActiveOnce();
                    MultiplayerStabilityMain.LogNoThrow(
                        "[SeqLock] Reached barrier #" + sequence + " (point " + pointId + ").");
                }

                float now = Time.realtimeSinceStartup;
                if (now - s_lastAnnounce[point] >= RetrySeconds)
                {
                    Announce(KindReach, point, sequence);
                    s_lastAnnounce[point] = now;
                }

                long key = Key(point, sequence);
                if (Get(key).Contains(NetworkingManager.PlayersReadyMask))
                {
                    Complete(point, sequence);
                    result = true;
                    return true;
                }

                if (now - s_activeSince[point] >= AbortSeconds)
                {
                    AbortEpoch(
                        "barrier #" + sequence + " point " + pointId + " exceeded "
                        + (int)AbortSeconds + " seconds",
                        true);
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                AbortEpoch("TryLock failed: " + e.Message, true);
                return false;
            }
        }

        internal static bool TryOnReceived(NetPlayer player, ReadOnlySpan<byte> bytes)
        {
            bool ours = HasMagic(bytes);
            if (!MultiplayerCompatibility.ProtocolsEnabled || s_aborted)
                return ours;

            if (!ours)
            {
                AbortEpoch(
                    "received a vanilla or foreign lock packet while sequenced protocol was active",
                    true);
                return false;
            }

            try
            {
                if (bytes.Length != FrameSize || bytes[4] != ProtocolVersion)
                {
                    AbortEpoch(
                        "invalid frame length/version " + bytes.Length + "/" +
                        (bytes.Length > 4 ? bytes[4].ToString() : "missing"),
                        true);
                    return true;
                }

                byte kind = bytes[5];
                byte point = bytes[6];
                int sequence = ReadInt32(bytes, 7);
                if (kind == KindAbort)
                {
                    AbortEpoch(
                        "peer " + player + " aborted at point " + point + " sequence " + sequence,
                        false);
                    return true;
                }
                if (kind != KindReach || sequence < 0)
                {
                    AbortEpoch("unknown or invalid frame kind/sequence", true);
                    return true;
                }

                long key = Key(point, sequence);
                s_groups[key] = Get(key).Add(player);
                return true;
            }
            catch (Exception e)
            {
                AbortEpoch("receive failed: " + e.Message, true);
                return true;
            }
        }

        internal static void ResetBaseline(string reason)
        {
            s_groups.Clear();
            s_nextSeq.Clear();
            s_activeSeq.Clear();
            s_activeSince.Clear();
            s_lastAnnounce.Clear();
            s_aborted = false;
            MultiplayerStabilityMain.LogNoThrow("[SeqLock] Baseline reset (" + reason + ").");
        }

        private static NetPlayerGroup Get(long key)
            => s_groups.TryGetValue(key, out NetPlayerGroup group)
                ? group
                : NetPlayerGroup.Empty;

        private static void Complete(byte point, int sequence)
        {
            s_activeSeq.Remove(point);
            s_activeSince.Remove(point);
            s_lastAnnounce.Remove(point);
            var stale = new List<long>();
            foreach (KeyValuePair<long, NetPlayerGroup> pair in s_groups)
            {
                if ((byte)(pair.Key >> 32) == point && (int)(uint)pair.Key <= sequence)
                    stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; i++)
                s_groups.Remove(stale[i]);
            MultiplayerStabilityMain.LogNoThrow(
                "[SeqLock] Barrier #" + sequence + " (point " + point + ") complete.");
        }

        private static void AbortEpoch(string reason, bool broadcast)
        {
            if (s_aborted)
                return;

            byte point = 0;
            int sequence = -1;
            foreach (KeyValuePair<byte, int> pair in s_activeSeq)
            {
                point = pair.Key;
                sequence = pair.Value;
                break;
            }

            if (broadcast)
            {
                for (int i = 0; i < 3; i++)
                    Announce(KindAbort, point, sequence);
            }

            s_aborted = true;
            s_groups.Clear();
            s_nextSeq.Clear();
            s_activeSeq.Clear();
            s_activeSince.Clear();
            s_lastAnnounce.Clear();
            MultiplayerStabilityMain.LogNoThrow(
                "[SeqLock][WARN] Sequenced epoch aborted; returning to vanilla barriers: " + reason);
        }

        private static bool Announce(byte kind, byte point, int sequence)
        {
            try
            {
                var frame = new byte[FrameSize];
                WriteUInt32(frame, 0, Magic);
                frame[4] = ProtocolVersion;
                frame[5] = kind;
                frame[6] = point;
                WriteInt32(frame, 7, sequence);
                bool sent = PhotonManager.Instance != null
                    && PhotonManager.Instance.SendMessageToOthers(8, frame, 0, frame.Length);
                if (!sent)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[SeqLock][WARN] send failed for kind=" + kind + " point=" + point
                        + " sequence=" + sequence + "; caller may retry.");
                }
                return sent;
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[SeqLock][WARN] send exception; caller may retry: " + e.Message);
                return false;
            }
        }

        private static void LogActiveOnce()
        {
            if (s_loggedActive)
                return;
            MultiplayerStabilityMain.LogNoThrow(
                "[SeqLock] Active under exact-build compatibility latch.");
            s_loggedActive = true;
        }

        private static bool HasMagic(ReadOnlySpan<byte> bytes)
            => bytes.Length >= 4 && ReadUInt32(bytes, 0) == Magic;

        private static void WriteInt32(byte[] buffer, int offset, int value)
            => WriteUInt32(buffer, offset, unchecked((uint)value));

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset)
            => (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));

        private static int ReadInt32(ReadOnlySpan<byte> buffer, int offset)
            => unchecked((int)ReadUInt32(buffer, offset));
    }

    [HarmonyPatch(typeof(LockNetManager), nameof(LockNetManager.Lock))]
    internal static class LockNetManager_Lock_Sequenced_Patch
    {
        private static bool Prefix(NetLockPointId pointId, ref bool __result)
        {
            if (!SequencedLocks.TryLock(pointId, out bool result))
                return true;
            __result = result;
            return false;
        }
    }

    [HarmonyPatch(typeof(LockNetManager), nameof(LockNetManager.OnLockReceived))]
    internal static class LockNetManager_OnLockReceived_Sequenced_Patch
    {
        private static bool Prefix(NetPlayer player, ReadOnlySpan<byte> bytes)
            => !SequencedLocks.TryOnReceived(player, bytes);
    }

    [HarmonyPatch(typeof(LockNetManager), nameof(LockNetManager.OnLeave))]
    internal static class LockNetManager_OnLeave_ResetSeq_Patch
    {
        private static void Postfix() => SequencedLocks.ResetBaseline("room leave");
    }

    [HarmonyPatch(typeof(SaveNetManager), nameof(SaveNetManager.UploadSave))]
    internal static class SaveNetManager_UploadSave_ResetSeq_Patch
    {
        private static void Prefix() => SequencedLocks.ResetBaseline("save upload");
    }

    [HarmonyPatch(typeof(SaveNetManager), nameof(SaveNetManager.DownloadSave))]
    internal static class SaveNetManager_DownloadSave_ResetSeq_Patch
    {
        private static void Prefix() => SequencedLocks.ResetBaseline("save download");
    }
}

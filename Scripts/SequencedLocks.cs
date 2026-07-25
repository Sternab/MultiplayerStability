// Sequenced co-op loading barriers.
//
// The loading path reuses NetLockPointId.LoadingProcess for several barriers in one area transition.
// Announcements carry no sequence number. If a fast peer announces the next barrier while another peer
// is still on the previous one, the announcement can be consumed by the previous accumulator. The slow
// peer then waits for a signal that has already been consumed.
//
// When every peer has the mod, code-8 announcements include a per-session barrier ordinal and signals
// are accumulated by ordinal. The baseline resets on save upload/download and room leave.
//
// If a barrier exceeds the timeout and every remote peer has reported a higher ordinal, it is treated as
// ordinal misalignment and force-completed. A slow peer that has not advanced does not satisfy this test.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.Networking;
using UnityEngine;

namespace MultiplayerStability
{
    internal static class SequencedLocks
    {
        private const float MisalignFallbackSeconds = 15f;

        // key = (pointId << 32) | ordinal  ->  players who have reached that (point, ordinal) barrier
        private static readonly Dictionary<long, NetPlayerGroup> s_Groups = new Dictionary<long, NetPlayerGroup>();
        private static readonly Dictionary<byte, int> s_NextSeq = new Dictionary<byte, int>();   // next ordinal to hand out per point
        private static readonly Dictionary<byte, int> s_ActiveSeq = new Dictionary<byte, int>();  // ordinal of the in-progress barrier per point
        private static readonly Dictionary<byte, float> s_ActiveSince = new Dictionary<byte, float>();

        private static long Key(byte pt, int seq) => ((long)pt << 32) | (uint)seq;

        internal static bool Active()
            => NetworkingManager.IsMultiplayer && AllPlayersModded();

        // Returns true and sets result when this replaces vanilla LockNetManager.Lock; false = run vanilla.
        internal static bool TryLock(NetLockPointId pointId, out bool result)
        {
            result = false;
            if (!Active())
                return false;
            try
            {
                byte pt = (byte)pointId;
                int seq;
                if (!s_ActiveSeq.TryGetValue(pt, out seq))
                {
                    seq = s_NextSeq.TryGetValue(pt, out var n) ? n : 0;
                    s_NextSeq[pt] = seq + 1;
                    s_ActiveSeq[pt] = seq;
                    s_ActiveSince[pt] = Time.realtimeSinceStartup;
                    long k0 = Key(pt, seq);
                    s_Groups[k0] = Get(k0).Add(NetworkingManager.LocalNetPlayer);
                    Announce(pt, seq);
                    MultiplayerStabilityMain.Log("[SeqLock] barrier #" + seq + " (point " + pointId + ") reached; announced.");
                }

                long k = Key(pt, seq);
                var ready = NetworkingManager.PlayersReadyMask;
                if (Get(k).Contains(ready))
                {
                    Complete(pt, seq, "all players present");
                    result = true;
                    return true;
                }

                if (Time.realtimeSinceStartup - s_ActiveSince[pt] > MisalignFallbackSeconds
                    && EveryRemotePlayerPastOrdinal(pt, seq))
                {
                    MultiplayerStabilityMain.Log("[SeqLock][WARN] barrier #" + seq + " stuck " + (int)MisalignFallbackSeconds
                        + "s with peers already past it -> ordinal misalignment; force-completing (resync if state looks off).");
                    Complete(pt, seq, "misalignment fallback");
                    result = true;
                    return true;
                }
                return true;   // still waiting -- consumed vanilla Lock, result stays false
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.Log("[SeqLock][ERR] TryLock, falling back to vanilla: " + e);
                return false;
            }
        }

        internal static bool TryOnReceived(NetPlayer player, ReadOnlySpan<byte> bytes)
        {
            if (!Active())
                return false;
            try
            {
                if (bytes.Length != 5)
                {
                    MultiplayerStabilityMain.Log("[SeqLock][ERR] expected 5-byte lock payload, got " + bytes.Length + " -- letting vanilla handle.");
                    return false;
                }
                byte pt = bytes[0];
                int seq = bytes[1] | (bytes[2] << 8) | (bytes[3] << 16) | (bytes[4] << 24);
                long k = Key(pt, seq);
                s_Groups[k] = Get(k).Add(player);
                return true;
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.Log("[SeqLock][ERR] TryOnReceived: " + e);
                return true;   // consumed; dropping one signal is safer than double-processing
            }
        }

        internal static void ResetBaseline(string reason)
        {
            if (s_Groups.Count == 0 && s_NextSeq.Count == 0 && s_ActiveSeq.Count == 0)
                return;
            s_Groups.Clear();
            s_NextSeq.Clear();
            s_ActiveSeq.Clear();
            s_ActiveSince.Clear();
            MultiplayerStabilityMain.Log("[SeqLock] baseline reset (" + reason + ").");
        }

        private static NetPlayerGroup Get(long key)
            => s_Groups.TryGetValue(key, out var g) ? g : NetPlayerGroup.Empty;

        private static void Complete(byte pt, int seq, string why)
        {
            s_ActiveSeq.Remove(pt);
            s_ActiveSince.Remove(pt);
            // Drop this and any older buckets for this point; only future (early-arrived) buckets survive.
            var stale = new List<long>();
            foreach (var kv in s_Groups)
            {
                if ((byte)(kv.Key >> 32) == pt && (int)(uint)kv.Key <= seq)
                    stale.Add(kv.Key);
            }
            foreach (var key in stale)
                s_Groups.Remove(key);
            MultiplayerStabilityMain.Log("[SeqLock] barrier #" + seq + " (point " + pt + ") complete (" + why + ").");
        }

        // True only if every remote required player has signalled some ordinal STRICTLY GREATER than seq
        // for this point -- provable "peer is ahead", which cannot occur for a slow-but-healthy peer.
        private static bool EveryRemotePlayerPastOrdinal(byte pt, int seq)
        {
            var union = NetPlayerGroup.Empty;
            foreach (var kv in s_Groups)
            {
                if ((byte)(kv.Key >> 32) == pt && (int)(uint)kv.Key > seq)
                    union = UnionInto(union, kv.Value);
            }
            union = union.Add(NetworkingManager.LocalNetPlayer);   // local is trivially "here"
            return union.Contains(NetworkingManager.PlayersReadyMask);
        }

        // NetPlayerGroup has no direct union; fold the ready mask's members that appear in either group.
        private static NetPlayerGroup UnionInto(NetPlayerGroup acc, NetPlayerGroup add)
        {
            foreach (var p in PhotonManager.Instance.ActivePlayers)
            {
                var np = p.NetPlayer;
                if (add.Contains(np))
                    acc = acc.Add(np);
            }
            return acc;
        }

        private static void Announce(byte pt, int seq)
        {
            var buf = new byte[5];
            buf[0] = pt;
            buf[1] = (byte)seq;
            buf[2] = (byte)(seq >> 8);
            buf[3] = (byte)(seq >> 16);
            buf[4] = (byte)(seq >> 24);
            if (!PhotonManager.Instance.SendMessageToOthers(8, buf, 0, 5))
                MultiplayerStabilityMain.Log("[SeqLock][ERR] failed to send sequenced lock #" + seq);
        }

        private static bool AllPlayersModded()
        {
            try
            {
                var photon = PhotonManager.Instance;
                if (photon == null)
                    return false;
                foreach (var info in photon.ActivePlayers)
                {
                    ModData[] mods;
                    if (!PhotonManager.Mods.TryGetModsData(info.UserId, out mods))
                        return false;
                    bool found = false;
                    foreach (var m in mods)
                    {
                        if (m.Id == MultiplayerStabilityMain.UniqueName) { found = true; break; }
                    }
                    if (!found)
                        return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(LockNetManager), nameof(LockNetManager.Lock))]
    internal static class LockNetManager_Lock_Sequenced_Patch
    {
        private static bool Prefix(NetLockPointId pointId, ref bool __result)
        {
            bool result;
            if (SequencedLocks.TryLock(pointId, out result))
            {
                __result = result;
                return false;   // skip vanilla
            }
            return true;        // vanilla
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

    // Every co-op (re)join and desync-resync routes through one of these; resetting here gives both clients
    // a common ordinal baseline for the identical barrier sequence that follows the transfer.
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

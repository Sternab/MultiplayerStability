// Sequenced co-op loading barriers -- fixes a vanilla engine race.
//
// The loading barrier (Game.AwaitingNetwork -> NetworkingManager.LockOn -> LockNetManager.Lock) uses ONE
// reusable lock id (NetLockPointId.LoadingProcess) for the SEVERAL barriers inside a single area
// transition, with no sequence number, and each client announces each barrier exactly once (edge on first
// reach, Photon code 8). If one client finishes a barrier and announces the NEXT one while the other is
// still on the previous barrier, that announcement is absorbed into the wrong barrier's accumulator: the
// slower client then waits forever for a signal that was already consumed -> "stuck at 100% while the host
// is already in the area" (field-diagnosed 2026-07-02; a latent vanilla bug any fast-vs-slow pair can hit,
// and the reason the FasterLoadTimes accelerator had to disable itself in co-op).
//
// Fix (active only when EVERY player runs this mod -- it changes the code-8 wire format): tag each barrier
// announcement with the sender's per-session barrier ORDINAL, and accumulate signals keyed by ordinal, so
// an early or late announcement lands in ITS OWN barrier's bucket and can never be absorbed by a
// neighbour. Ordinals stay aligned across clients for free: a barrier completes only when ALL players have
// signalled it, so no client can advance its ordinal past another's by more than the in-flight barrier --
// the barrier mechanism self-synchronises the count. The only shared requirement is a common baseline,
// which we reset at every co-op save-transfer (initial join, rejoin, and desync-resync all route through
// UploadSave/DownloadSave) and on leaving the room.
//
// Safety net: if a sequenced barrier is stuck past a timeout AND every remote player has provably moved to
// a HIGHER ordinal (only possible under ordinal misalignment -- never under a merely-slow peer, which
// would have sent no higher-ordinal signal), force-complete and log loudly, converting a would-be
// permanent hang into a recoverable event (host can resync). A slow-but-healthy load never trips this.
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

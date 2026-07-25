// Desync diagnostics. In the release build, the potential-desync handler list is empty, mismatches
// shorter than two seconds are silent, and the warning dialog fires once per session through the
// WasDesync latch. This component adds attribution without initiating a resync:
//   1. injects a logging IDesyncHandler into both handler lists of the live detection strategy;
//   2. records (tick, localHash) each tick and, on mismatch, logs every peer's reported hash plus a
//      clearly-labelled tick%5 bucket heuristic. SynchronizedData carries no sender tick, and a 32-bit
//      hash may match several local ticks, so this attribution never drives player-facing policy;
//   3. traces which serializable RNG streams advanced each sim tick, so randomState desyncs identify the
//      affected stream (e.g. Weather advancing per frame);
//   4. re-arms the once-per-session latch when an episode recovers (~5s of matching hashes), so later
//      desyncs still notify. While continuously diverged nothing re-fires, so no dialog spam;
//   5. marks transition-adjacent reports in the log. The vanilla UIDesyncHandler always remains active.
using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Controllers.Net;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities.Base;
using Kingmaker.EntitySystem.Persistence;
using Kingmaker.GameModes;
using Kingmaker.Mechanics.Entities;
using Kingmaker.Networking;
using Kingmaker.Networking.Desync;
using Kingmaker.Networking.Hash;
using Kingmaker.Utility.Random;
using Kingmaker.Utility.StatefulRandom;
using StateHasher.Core;
using StateHasher.Core.Hashers;
using UnityEngine;

namespace MultiplayerStability
{
    internal static class DesyncWatch
    {
        private static readonly string[] BucketNames =
            { "player", "sceneEntities", "areaPersistent", "randomState", "syncData+signals" };

        private const int HashRingSize = 128;
        private static readonly int[] s_hashRingTick = new int[HashRingSize];
        private static readonly int[] s_hashRingHash = new int[HashRingSize];
        private static int s_hashRingCount;

        // ~30s of per-tick RNG history at 20 tps, so a desync dump reaches back far enough to catch the
        // FIRST diverging tick (a stream can diverge long before detection while others stay in sync).
        private const int RngRingSize = 600;
        private static readonly int[] s_rngRingTick = new int[RngRingSize];
        private static readonly string[] s_rngRingStreams = new string[RngRingSize];
        private static int s_rngRingCount;
        private static RandState[] s_prevStates;
        private static int s_lastObservedTick = int.MinValue;

        // Entity/fact-creation attribution: every GlobalUuid draw comes from minting an entity or fact
        // UniqueId. When GlobalUuid is the diverged stream (different creation COUNT between clients), this
        // ring names WHICH blueprints were created near the divergence, so the two logs can be diffed to
        // the exact entity one client made and the other didn't.
        private const int UuidRingSize = 1024;
        private static readonly int[] s_uuidRingTick = new int[UuidRingSize];
        private static readonly string[] s_uuidRingWhat = new string[UuidRingSize];
        private static int s_uuidRingCount;

        private static BaseDesyncDetectionStrategy s_strategy;
        private static BaseDesyncDetectionStrategy s_wiredStrategy;
        private static DateTime s_nextWireAttemptUtc;
        private static bool s_hadDesync;
        private static bool s_rearmReflectionErrorLogged;
        // Plain negative sentinel: MinValue sentinels overflowed the rate-limit subtraction TWICE (at int
        // width in 0.4.x and again at long width after a refactor), silencing every mismatch log.
        private static long s_lastMismatchLogTick = -1000000;

        // Transition proximity is diagnostic context only. It must not suppress a dialog: RuleSystem lives
        // in randomState, and SynchronizedData does not identify the sender tick needed to attribute a
        // reported 32-bit hash safely.
        private const int TransitionGraceTicks = 100;   // ~5s: "during or just-after" loading/cutscene/fade
        private static long s_lastTransitionTick = -1000000;

        internal static void Wire()
        {
            if (s_strategy == null && DateTime.UtcNow < s_nextWireAttemptUtc)
                return;
            try
            {
                var sync = PhotonManager.Sync;
                var live = AccessTools.Field(typeof(SyncNetManager), "m_DesyncDetectionStrategy")
                    ?.GetValue(sync) as BaseDesyncDetectionStrategy;
                if (live == null)
                {
                    s_strategy = null;
                    s_nextWireAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                    MultiplayerStabilityMain.LogNoThrow(
                        "[DesyncWatch][WARN] detection strategy unavailable; retrying in five seconds.");
                    return;
                }
                s_strategy = live;
                if (ReferenceEquals(live, s_wiredStrategy))
                    return;

                InjectHandler(live, "m_PotentialDesyncHandler", "potential");
                InjectHandler(live, "m_SeriousDesyncHandler", "serious");
                s_wiredStrategy = live;
                s_nextWireAttemptUtc = DateTime.MinValue;
                MultiplayerStabilityMain.LogNoThrow(
                    "[DesyncWatch] Logging handlers injected into " + live.GetType().Name + ".");
            }
            catch (Exception e)
            {
                s_strategy = null;
                s_nextWireAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                MultiplayerStabilityMain.LogNoThrow(
                    "[DesyncWatch][ERR] wire failed; retrying in five seconds: " + e);
            }
        }

        private static void InjectHandler(
            BaseDesyncDetectionStrategy strategy,
            string fieldName,
            string kind)
        {
            var field = AccessTools.Field(strategy.GetType(), fieldName);
            if (field == null)
                throw new MissingFieldException(strategy.GetType().FullName, fieldName);

            var existing = field.GetValue(strategy) as IDesyncHandler;
            var ours = new LoggingDesyncHandler(kind);
            // The vanilla handlers are CompositeDesyncHandlers over a mutable list -- append when possible,
            // otherwise wrap whatever is there in a new composite.
            var collectors = (existing is CompositeDesyncHandler)
                ? AccessTools.Field(typeof(CompositeDesyncHandler), "m_Collectors")
                    ?.GetValue(existing) as List<IDesyncHandler>
                : null;
            if (collectors != null)
            {
                for (int i = 0; i < collectors.Count; i++)
                {
                    if (collectors[i] is LoggingDesyncHandler logging && logging.m_Kind == kind)
                        return;
                }
                collectors.Add(ours);
                return;
            }
            if (existing is LoggingDesyncHandler direct && direct.m_Kind == kind)
                return;

            var list = new List<IDesyncHandler>();
            if (existing != null)
                list.Add(existing);
            list.Add(ours);
            field.SetValue(strategy, new CompositeDesyncHandler(list));
        }

        // Hash every scene entity as a standalone root. RecursiveReferences is reset around each entity so
        // an earlier traversal cannot change a later result. Full 128-bit hashes and sorted identities make
        // two peers' output mechanically diffable. These are decomposition fingerprints, not a promise that
        // concatenating them reproduces the engine's aggregate scene hash.
        private static void DumpSceneEntityHashes(HashableState data)
        {
            try
            {
                var scene = data.sceneEntitiesState;
                var entities = scene?.AllEntityData;
                if (entities == null)
                    return;
                var lines = new List<string>(entities.Count);
                foreach (var e in entities)
                {
                    if (e == null)
                        continue;
                    try
                    {
                        RecursiveReferences.Reset();
                        Hash128 hash = ClassHasher<Entity>.GetHash128(e);
                        // Entity.ToString() reads View.GO.name, which is client-local and makes an otherwise
                        // useful cross-peer diff noisy. Type plus synchronized UniqueId is the stable key.
                        string id = e.GetType().Name + "#" + (e.UniqueId ?? "?");
                        lines.Add(id + " = " + hash);
                    }
                    finally
                    {
                        RecursiveReferences.Reset();
                    }
                }
                lines.Sort(StringComparer.Ordinal);
                const int LinesPerChunk = 200;
                for (int start = 0; start < lines.Count; start += LinesPerChunk)
                {
                    int end = Math.Min(start + LinesPerChunk, lines.Count);
                    var sb = new StringBuilder(
                        "[DesyncWatch] standalone scene entity hashes ")
                        .Append(start + 1).Append('-').Append(end).Append('/').Append(lines.Count)
                        .Append(':');
                    for (int i = start; i < end; i++)
                        sb.Append("\n  ").Append(lines[i]);
                    MultiplayerStabilityMain.LogNoThrow(sb.ToString());
                }
            }
            catch (Exception ex)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[DesyncWatch][ERR] scene entity hash dump: " + ex.Message);
            }
        }

        private sealed class LoggingDesyncHandler : IDesyncHandler
        {
            internal readonly string m_Kind;
            public LoggingDesyncHandler(string kind) { m_Kind = kind; }

            public void RaiseDesync(HashableState data, DesyncMeta meta)
            {
                try
                {
                    bool transitionAdjacent = InTransitionWindow();
                    MultiplayerStabilityMain.LogNoThrow(
                        "[DesyncWatch] ===== " + m_Kind.ToUpper() + " DESYNC detected @tick "
                        + meta.Tick + " room=" + meta.RoomId + " players=" + meta.PlayersCount
                        + " transitionAdjacent=" + transitionAdjacent + " =====");
                    DumpRngRing(meta.Tick);
                    // Per-entity state fingerprints for sceneEntities-bucket desyncs (the RNG ring covers
                    // randomState). We are called inside HandleDesync's active StateHasherContext, so the
                    // recursive-reference bookkeeping ClassHasher needs is already live. Serious-only to
                    // bound the cost (hashing every entity is a one-off hitch on an already-diverged session).
                    if (m_Kind == "serious")
                    {
                        DumpSceneEntityHashes(data);
                        MultiplayerStabilityMain.LogNoThrow(
                            "[DesyncWatch] Vanilla desync dialog remains authoritative; no diagnostic "
                            + "attribution suppresses it.");
                    }
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.LogNoThrow("[DesyncWatch][ERR] handler: " + e);
                }
            }
        }

        internal static void ResetRuntimeState(string reason)
        {
            s_hashRingCount = 0;
            s_rngRingCount = 0;
            s_uuidRingCount = 0;
            s_prevStates = null;
            s_hadDesync = false;
            s_rearmReflectionErrorLogged = false;
            s_lastMismatchLogTick = -1000000;
            s_lastTransitionTick = -1000000;
            s_lastObservedTick = int.MinValue;
            // The game can replace the strategy object across network sessions. Force the next simulation
            // tick to resolve the live object; InjectHandler is idempotent if the instance was retained.
            s_strategy = null;
            MultiplayerStabilityMain.LogNoThrow(
                "[DesyncWatch] Runtime history reset (" + reason + ").");
        }

        // Once per simulation tick (prefix on SyncStateCheckerController's tick, main thread), just before
        // vanilla computes the tick hash: record which serializable RNG streams moved since last tick.
        internal static void OnSimTick()
        {
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return;
                if (s_strategy == null)
                    Wire();
                int tick = Game.Instance.RealTimeController.CurrentNetworkTick;
                if (s_lastObservedTick != int.MinValue && tick < s_lastObservedTick)
                    ResetRuntimeState("network tick regression " + s_lastObservedTick + " -> " + tick);
                s_lastObservedTick = tick;
                var streams = PFStatefulRandom.Serializable;
                if (s_prevStates == null || s_prevStates.Length != streams.Length)
                {
                    s_prevStates = new RandState[streams.Length];
                    for (int i = 0; i < streams.Length; i++)
                        s_prevStates[i] = streams[i].State;
                }
                else
                {
                    StringBuilder sb = null;
                    for (int i = 0; i < streams.Length; i++)
                    {
                        var cur = streams[i].State;
                        var prev = s_prevStates[i];
                        if (cur.x != prev.x || cur.y != prev.y || cur.z != prev.z || cur.w != prev.w)
                        {
                            if (sb == null) sb = new StringBuilder();
                            else sb.Append(',');
                            sb.Append(streams[i].Name).Append(':')
                              .Append(cur.x.ToString("X8")).Append('/')
                              .Append(cur.y.ToString("X8")).Append('/')
                              .Append(cur.z.ToString("X8")).Append('/')
                              .Append(cur.w.ToString("X8"));
                            s_prevStates[i] = cur;
                        }
                    }
                    if (sb != null)
                    {
                        int slot = s_rngRingCount++ % RngRingSize;
                        s_rngRingTick[slot] = tick;
                        s_rngRingStreams[slot] = sb.ToString();
                    }
                }
                // Remember the last tick we were in a transition (loading / fade / cutscene).
                // InTransitionWindow() also covers the short settle period after an area load.
                if (IsTransitionActive())
                    s_lastTransitionTick = tick;
            }
            catch (Exception)
            {
            }
        }

        // True while a loading/fade screen is up or a cutscene mode is active -- the contexts where
        // frame-timing RNG flaps happen. LoadingProcess covers loading + fade; CurrentMode covers scripted
        // cutscene sequences (which advance the Cutscene/CutsceneAttack streams from view timing).
        private static bool IsTransitionActive()
        {
            try
            {
                var lp = LoadingProcess.Instance;
                if (lp != null && (lp.IsLoadingInProcess || lp.IsLoadingScreenActive))
                    return true;
                var mode = Game.Instance.CurrentMode;
                return mode == GameModeType.Cutscene || mode == GameModeType.CutsceneGlobalMap;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // In or just-after a transition (grace window absorbs the settle-out after an area load completes).
        private static bool InTransitionWindow()
        {
            try
            {
                long now = Game.Instance.RealTimeController.CurrentNetworkTick;
                return now - s_lastTransitionTick <= TransitionGraceTicks;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Postfix on HashCalculator.GetStateHashByNewMethod: remember (tick, ourHash) so a mismatch can be
        // traced back to the sender tick and its tick%5 bucket. (The method's own parameter is ignored by
        // the implementation -- SplitState reads CurrentNetworkTick itself -- so we do the same.)
        internal static void RecordLocalHash(int hash)
        {
            try
            {
                int slot = s_hashRingCount++ % HashRingSize;
                s_hashRingTick[slot] = Game.Instance.RealTimeController.CurrentNetworkTick;
                s_hashRingHash[slot] = hash;
            }
            catch (Exception)
            {
            }
        }

        // Postfix on SyncStateCheckerController.CheckHash (every sim tick): drives the re-arm edge check.
        internal static void AfterCheckHash()
        {
            try
            {
                ReArmOnRecovery();
            }
            catch (Exception)
            {
            }
        }

        // Postfix on SyncNetManager.HandleActorsState -- vanilla's CheckHash calls it exactly and only when
        // its own comparison found a mismatch, so no re-derived comparison logic can drift out of sync with
        // the game's (v0.4.2's local re-compare never fired in the field; this seam cannot miss).
        internal static void OnMismatchReported()
        {
            try
            {
                int tick = Game.Instance.RealTimeController.CurrentNetworkTick;
                // long arithmetic: with the int.MinValue sentinel, int subtraction overflows negative and
                // silently swallowed every mismatch log in the first two field captures.
                if ((long)tick - s_lastMismatchLogTick < 20)   // one line per second while diverged
                    return;
                s_lastMismatchLogTick = tick;

                var players = Game.Instance.SynchronizedDataController.SynchronizedData.Players;
                var sb = new StringBuilder("[DesyncWatch] hash mismatch @tick ").Append(tick).Append(": ");
                foreach (var pc in players)
                {
                    int hash = 0;
                    bool have = false;
                    foreach (var sd in pc.Commands)
                    {
                        if (!sd.IsEmpty) { hash = sd.stateHash; have = true; }
                    }
                    if (have)
                        sb.Append('P').Append(pc.Player.Index).Append('=').Append(hash.ToString("X8")).Append(' ');
                }
                sb.Append("-> ").Append(InferBucket(players));
                sb.Append(" transitionAdjacent=").Append(InTransitionWindow());
                MultiplayerStabilityMain.LogNoThrow(sb.ToString());
            }
            catch (Exception)
            {
            }
        }

        private static string InferBucket(List<PlayerCommands<SynchronizedData>> players)
        {
            var reportedHashes = new HashSet<int>();
            foreach (var pc in players)
            {
                foreach (var sd in pc.Commands)
                {
                    if (!sd.IsEmpty)
                        reportedHashes.Add(sd.stateHash);
                }
            }

            int n = Math.Min(s_hashRingCount, HashRingSize);
            int first = Math.Max(0, s_hashRingCount - n);
            int? bucket = null;
            int matches = 0;
            for (int i = first; i < s_hashRingCount; i++)
            {
                int slot = i % HashRingSize;
                if (!reportedHashes.Contains(s_hashRingHash[slot]))
                    continue;

                int candidate = ((s_hashRingTick[slot] % BucketNames.Length) + BucketNames.Length)
                    % BucketNames.Length;
                matches++;
                if (!bucket.HasValue)
                    bucket = candidate;
                else if (bucket.Value != candidate)
                {
                    return "bucket=? (ambiguous hash-only heuristic; " + matches
                        + "+ local matches span multiple buckets)";
                }
            }

            if (!bucket.HasValue)
            {
                return "bucket=? (no local hash match)";
            }

            string bucketName = BucketNames[bucket.Value];
            return "bucket~=" + bucketName + " (hash-only heuristic; " + matches
                + " local match" + (matches == 1 ? "" : "es") + ", sender tick unavailable)";
        }

        // Vanilla latches WasDesync for the whole session after the first serious desync. Once an episode
        // recovers (HasDesync self-clears after ~100 matching ticks), clear the latch and the window ticks
        // so the NEXT episode notifies again. No spam: HasDesync stays true while continuously diverged.
        private static void ReArmOnRecovery()
        {
            var strat = s_strategy;
            if (strat == null)
                return;
            bool has = strat.HasDesync;
            if (s_hadDesync && !has && strat.WasDesync)
            {
                var setter = AccessTools.PropertySetter(
                    typeof(BaseDesyncDetectionStrategy), "WasDesync");
                var firstField = AccessTools.Field(strat.GetType(), "m_DesyncTickFirst");
                var lastField = AccessTools.Field(strat.GetType(), "m_DesyncTickLast");
                if (setter == null || firstField == null || lastField == null)
                {
                    if (!s_rearmReflectionErrorLogged)
                    {
                        s_rearmReflectionErrorLogged = true;
                        MultiplayerStabilityMain.LogNoThrow(
                            "[DesyncWatch][ERR] recovery latch members not found; later "
                            + "desync notifications may remain latched.");
                    }
                    s_hadDesync = has;
                    return;
                }

                try
                {
                    setter.Invoke(strat, new object[] { false });
                    firstField.SetValue(strat, -32768);
                    lastField.SetValue(strat, -32768);
                }
                catch (Exception e)
                {
                    if (!s_rearmReflectionErrorLogged)
                    {
                        s_rearmReflectionErrorLogged = true;
                        MultiplayerStabilityMain.LogNoThrow(
                            "[DesyncWatch][ERR] recovery latch reset failed; later desync "
                            + "notifications may remain latched: " + e.Message);
                    }
                    s_hadDesync = has;
                    return;
                }
                MultiplayerStabilityMain.LogNoThrow(
                    "[DesyncWatch] Desync episode recovered (hashes matched ~5s); notifications re-armed.");
            }
            s_hadDesync = has;
        }

        private static void DumpRngRing(int aroundTick)
        {
            int n = Math.Min(s_rngRingCount, RngRingSize);
            if (n == 0)
            {
                MultiplayerStabilityMain.LogNoThrow("[DesyncWatch] rng trace: (empty)");
                return;
            }
            var sb = new StringBuilder("[DesyncWatch] rng streams advanced near tick ").Append(aroundTick).Append(':');
            int emitted = 0;
            for (int i = Math.Max(0, s_rngRingCount - RngRingSize); i < s_rngRingCount && emitted < 400; i++)
            {
                int slot = i % RngRingSize;
                if (s_rngRingTick[slot] < aroundTick - RngRingSize)
                    continue;
                sb.Append("\n  t").Append(s_rngRingTick[slot]).Append(": ").Append(s_rngRingStreams[slot]);
                emitted++;
            }
            MultiplayerStabilityMain.LogNoThrow(sb.ToString());
            DumpUuidRing(aroundTick);
        }

        // Called from a postfix on EntityFact.Attach -- the fact/entity UniqueId mint that draws GlobalUuid.
        internal static void RecordUuidCreation(string what)
        {
            try
            {
                int slot = s_uuidRingCount++ % UuidRingSize;
                s_uuidRingTick[slot] = Game.Instance.RealTimeController.CurrentNetworkTick;
                s_uuidRingWhat[slot] = what;
            }
            catch (Exception)
            {
            }
        }

        private static void DumpUuidRing(int aroundTick)
        {
            int n = Math.Min(s_uuidRingCount, UuidRingSize);
            if (n == 0)
                return;
            var sb = new StringBuilder("[DesyncWatch] entities/facts created near tick ").Append(aroundTick).Append(':');
            // Emit cap high enough that a big combat-start buff shower can't crowd out the entities created
            // in the ticks AFTER it (the divergent one has twice landed just past a 120 cap).
            int emitted = 0;
            for (int i = Math.Max(0, s_uuidRingCount - UuidRingSize); i < s_uuidRingCount && emitted < 700; i++)
            {
                int slot = i % UuidRingSize;
                if (s_uuidRingTick[slot] < aroundTick - RngRingSize)
                    continue;
                sb.Append("\n  t").Append(s_uuidRingTick[slot]).Append(": ").Append(s_uuidRingWhat[slot]);
                emitted++;
            }
            MultiplayerStabilityMain.LogNoThrow(sb.ToString());
        }
    }

    [HarmonyPatch]
    internal static class SyncStateCheckerController_Tick_RngTrace_Patch
    {
        private static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(typeof(SyncStateCheckerController), "Kingmaker.Controllers.Interfaces.IControllerTick.Tick");
        private static void Prefix() => DesyncWatch.OnSimTick();
    }

    [HarmonyPatch]
    internal static class SyncStateCheckerController_CheckHash_Attribution_Patch
    {
        private static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(typeof(SyncStateCheckerController), "CheckHash");
        private static void Postfix() => DesyncWatch.AfterCheckHash();
    }

    // Attribution for GlobalUuid divergence: record every fact/entity UniqueId mint (only in MP, only when
    // the id is freshly created -- not on load/restore). Named by blueprint so the two clients' logs diff
    // to the exact entity one made and the other didn't.
    [HarmonyPatch(typeof(EntityFact), nameof(EntityFact.Attach))]
    internal static class EntityFact_Attach_UuidTrace_Patch
    {
        private static void Prefix(EntityFact __instance, out bool __state)
        {
            __state = false;
            try
            {
                // Only a first-time attach (empty UniqueId) actually draws GlobalUuid; re-attach/load does not.
                __state = NetworkingManager.IsMultiplayer
                    && __instance != null
                    && string.IsNullOrEmpty(__instance.UniqueId);
            }
            catch (Exception)
            {
                // Diagnostic only: never make EntityFact.Attach fail.
            }
        }

        private static void Postfix(EntityFact __instance, bool __state)
        {
            if (!__state)
                return;
            try
            {
                var bp = __instance.Blueprint;
                var owner = __instance.Owner;
                // Owner identified by blueprint name, not just type: the capture-12 fork was Pascal's
                // ally-auras buffing an extra "ally" that exists on ONE machine only (suspected level-up
                // plan-unit ghost) -- naming the RECIPIENT is what identifies such ghosts. IsPreview units
                // are additionally marked, since stream-safe preview copies also pass through here and
                // otherwise masquerade as real creation forks in the diff.
                string who;
                var ownerUnit = owner as AbstractUnitEntity;
                if (ownerUnit != null)
                    who = "@" + (ownerUnit.Blueprint != null ? ownerUnit.Blueprint.name : "?")
                        + "#" + (ownerUnit.UniqueId ?? "?")
                        + (ownerUnit.IsPreviewUnit ? "[PREVIEW]" : "");
                else
                {
                    var ownerEntity = owner as Entity;
                    who = ownerEntity != null
                        ? "@" + ownerEntity.GetType().Name + "#" + (ownerEntity.UniqueId ?? "?")
                        : owner != null ? "@" + owner.GetType().Name : "";
                }
                DesyncWatch.RecordUuidCreation((bp != null ? bp.name : "?") + who);
            }
            catch (Exception)
            {
            }
        }
    }

    [HarmonyPatch(typeof(HashCalculator), nameof(HashCalculator.GetStateHashByNewMethod))]
    internal static class HashCalculator_GetStateHashByNewMethod_Ring_Patch
    {
        private static void Postfix(int __result) => DesyncWatch.RecordLocalHash(__result);
    }

    [HarmonyPatch(typeof(SyncNetManager), nameof(SyncNetManager.HandleActorsState))]
    internal static class SyncNetManager_HandleActorsState_Attribution_Patch
    {
        private static void Postfix() => DesyncWatch.OnMismatchReported();
    }

    [HarmonyPatch(typeof(ModsNetManager), nameof(ModsNetManager.OnLeave))]
    internal static class ModsNetManager_OnLeave_DesyncWatchReset_Patch
    {
        private static void Postfix() => DesyncWatch.ResetRuntimeState("room leave");
    }
}

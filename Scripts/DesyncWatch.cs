// Desync diagnostics. In the release build, the potential-desync handler list is empty, mismatches
// shorter than two seconds are silent, and the warning dialog fires once per session through the
// WasDesync latch. This component adds attribution without initiating a resync:
//   1. injects a logging IDesyncHandler into both handler lists of the live detection strategy;
//   2. records (tick, localHash) each tick and, on mismatch, logs every peer's reported hash plus the
//      inferred tick%5 state bucket (player/sceneEntities/areaPersistent/randomState/syncData+signals);
//   3. traces which serializable RNG streams advanced each sim tick, so randomState desyncs identify the
//      affected stream (e.g. Weather advancing per frame);
//   4. re-arms the once-per-session latch when an episode recovers (~5s of matching hashes), so later
//      desyncs still notify. While continuously diverged nothing re-fires, so no dialog spam;
//   5. suppresses the vanilla resync DIALOG (UIDesyncHandler.ShowMessageBox, via a prefix) for confirmed
//      randomState-only desyncs that fire during a loading/cutscene/fade transition (frame-timing flaps that
//      self-heal) -- still logged in full; the dialog is re-shown if the episode graduates to another state
//      bucket or persists past the transition.
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

        // Entity/fact-creation attribution: every GlobalUuid draw comes from minting an entity or fact
        // UniqueId. When GlobalUuid is the diverged stream (different creation COUNT between clients), this
        // ring names WHICH blueprints were created near the divergence, so the two logs can be diffed to
        // the exact entity one client made and the other didn't.
        private const int UuidRingSize = 1024;
        private static readonly int[] s_uuidRingTick = new int[UuidRingSize];
        private static readonly string[] s_uuidRingWhat = new string[UuidRingSize];
        private static int s_uuidRingCount;

        private static BaseDesyncDetectionStrategy s_strategy;
        private static bool s_wireAttempted;
        private static bool s_hadDesync;
        // Plain negative sentinel: MinValue sentinels overflowed the rate-limit subtraction TWICE (at int
        // width in 0.4.x and again at long width after a refactor), silencing every mismatch log.
        private static long s_lastMismatchLogTick = -1000000;

        // Transition-flap policy (v0.7.6): randomState-only desyncs during loading/cutscene/fade are transient
        // frame-timing skews (Cutscene/LoadingScreen/Weather/UnitRandom/Customization/GlobalUuid/Animation
        // streams reconverging) -- they self-heal and are not gameplay forks. We still LOG them, but hold the
        // player-facing "resync" prompt unless the episode GRADUATES to a non-randomState bucket or PERSISTS
        // past the transition. NOTE: randomState alone is NOT safe to downgrade (RuleSystem lives there -- the
        // burst fork was randomState in active combat); the transition window is the load-bearing qualifier.
        private const int TransitionGraceTicks = 100;   // ~5s: "during or just-after" loading/cutscene/fade
        private const int PersistTicks = 200;           // ~10s diverged past the flap -> escalate (not transient)
        private static long s_lastTransitionTick = -1000000;
        private static bool s_episodeSawRandomState;     // POSITIVELY confirmed randomState this episode (gate:
                                                          // require this, not mere absence of a contrary bucket)
        private static bool s_episodeSawNonRandomState;
        private static bool s_episodeBoxSuppressed;       // we held vanilla's desync dialog as a transition flap
        private static bool s_episodeEscalated;
        private static long s_episodeStartTick;
        private static string s_lastBucketName;

        internal static void Wire()
        {
            if (s_wireAttempted)
                return;
            s_wireAttempted = true;
            try
            {
                var sync = PhotonManager.Sync;
                s_strategy = AccessTools.Field(typeof(SyncNetManager), "m_DesyncDetectionStrategy")
                    ?.GetValue(sync) as BaseDesyncDetectionStrategy;
                if (s_strategy == null)
                {
                    MultiplayerStabilityMain.Log("[DesyncWatch][ERR] detection strategy not found; diagnosis inactive.");
                    return;
                }
                InjectHandler("m_PotentialDesyncHandler", "potential");
                InjectHandler("m_SeriousDesyncHandler", "serious");
                MultiplayerStabilityMain.Log("[DesyncWatch] Logging handlers injected into " + s_strategy.GetType().Name + ".");
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.Log("[DesyncWatch][ERR] wire failed: " + e);
            }
        }

        private static void InjectHandler(string fieldName, string kind)
        {
            var field = AccessTools.Field(s_strategy.GetType(), fieldName);
            var existing = field?.GetValue(s_strategy) as IDesyncHandler;
            var ours = new LoggingDesyncHandler(kind);
            // The vanilla handlers are CompositeDesyncHandlers over a mutable list -- append when possible,
            // otherwise wrap whatever is there in a new composite.
            var collectors = (existing is CompositeDesyncHandler)
                ? AccessTools.Field(typeof(CompositeDesyncHandler), "m_Collectors").GetValue(existing) as List<IDesyncHandler>
                : null;
            if (collectors != null)
            {
                collectors.Add(ours);
                return;
            }
            var list = new List<IDesyncHandler>();
            if (existing != null)
                list.Add(existing);
            list.Add(ours);
            field?.SetValue(s_strategy, new CompositeDesyncHandler(list));
        }

        // Hash every scene entity individually and log (identity, hash). Two clients' logs diff to the exact
        // entity whose hashed state diverged -- the sceneEntities-bucket counterpart to the RNG fingerprints.
        private static void DumpSceneEntityHashes(HashableState data)
        {
            try
            {
                var scene = data.sceneEntitiesState;
                var entities = scene?.AllEntityData;
                if (entities == null)
                    return;
                var sb = new StringBuilder("[DesyncWatch] scene entity hashes (diff vs peer to name the diverged entity):");
                int n = 0;
                foreach (var e in entities)
                {
                    if (e == null)
                        continue;
                    if (n >= 500)
                    {
                        sb.Append("\n  ...(truncated at 500 of ").Append(entities.Count).Append(')');
                        break;
                    }
                    Hash128 h = ClassHasher<Entity>.GetHash128(e);
                    string id = e.ToString();
                    if (id != null && id.Length > 90)
                        id = id.Substring(0, 90);
                    sb.Append("\n  ").Append(id).Append(" = ").Append(((uint)h.GetHashCode()).ToString("X8"));
                    n++;
                }
                MultiplayerStabilityMain.Log(sb.ToString());
            }
            catch (Exception ex)
            {
                MultiplayerStabilityMain.Log("[DesyncWatch][ERR] scene entity hash dump: " + ex.Message);
            }
        }

        private sealed class LoggingDesyncHandler : IDesyncHandler
        {
            private readonly string m_Kind;
            public LoggingDesyncHandler(string kind) { m_Kind = kind; }

            public void RaiseDesync(HashableState data, DesyncMeta meta)
            {
                try
                {
                    // The vanilla POTENTIAL handler list is empty, so our injected logger is the only thing in
                    // it -- and ReportState fires POTENTIAL exactly ONCE at the true start of a divergence
                    // stretch (~41 ticks before serious). That is the correct point to reset the per-episode
                    // flags: they then accumulate cleanly through potential->serious, and nothing wipes the
                    // suppress flag on the serious tick. (HasDesync only turns true AT the serious threshold,
                    // so a HasDesync-rising-edge reset fired on the serious tick and cleared the just-set
                    // suppress flag, breaking escalation.)
                    if (m_Kind != "serious")
                        ResetEpisodeFlags();

                    // The ACTUAL player-facing dialog is vanilla UIDesyncHandler.ShowMessageBox; it runs earlier
                    // in the same composite dispatch and its prefix (below) already decided whether to suppress
                    // it as a transition flap, recording s_episodeBoxSuppressed. Our log wording just mirrors
                    // that decision -- we never controlled the prompt from here (a v0.7.6 mistake caught in review).
                    bool flap = m_Kind == "serious" && s_episodeBoxSuppressed;

                    string header = flap
                        ? "TRANSITION DESYNC FLAP (confirmed randomState-only during loading/cutscene; expected to self-heal)"
                        : m_Kind.ToUpper() + " DESYNC detected";
                    MultiplayerStabilityMain.Log("[DesyncWatch] ===== " + header + " @tick "
                        + meta.Tick + " room=" + meta.RoomId + " players=" + meta.PlayersCount + " =====");
                    DumpRngRing(meta.Tick);
                    // Per-entity state fingerprints for sceneEntities-bucket desyncs (the RNG ring covers
                    // randomState). We are called inside HandleDesync's active StateHasherContext, so the
                    // recursive-reference bookkeeping ClassHasher needs is already live. Serious-only to
                    // bound the cost (hashing every entity is a one-off hitch on an already-diverged session).
                    if (m_Kind == "serious")
                    {
                        DumpSceneEntityHashes(data);
                        if (flap)
                            MultiplayerStabilityMain.Log("[DesyncWatch] Vanilla resync dialog HELD (transient transition flap)"
                                + " -- will show it only if this persists past the transition (~10s) or hits another state bucket.");
                        else
                            MultiplayerStabilityMain.Log("[DesyncWatch] You can keep playing, or the host can resync everyone"
                                + " from the co-op lobby (Launch) -- transfers are fast with this mod.");
                    }
                }
                catch (Exception e)
                {
                    MultiplayerStabilityMain.Log("[DesyncWatch][ERR] handler: " + e);
                }
            }
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
                var streams = PFStatefulRandom.Serializable;
                if (s_prevStates == null || s_prevStates.Length != streams.Length)
                {
                    s_prevStates = new RandState[streams.Length];
                    for (int i = 0; i < streams.Length; i++)
                        s_prevStates[i] = streams[i].State;
                    return;
                }
                StringBuilder sb = null;
                for (int i = 0; i < streams.Length; i++)
                {
                    var cur = streams[i].State;
                    var prev = s_prevStates[i];
                    if (cur.x != prev.x || cur.y != prev.y || cur.z != prev.z || cur.w != prev.w)
                    {
                        if (sb == null) sb = new StringBuilder();
                        else sb.Append(',');
                        // Append a post-tick state fingerprint: two clients drawing the same streams on the
                        // same tick look identical without it; the fingerprint diff pinpoints the exact tick
                        // and stream associated with the first recorded divergence (2026-07-02 capture).
                        sb.Append(streams[i].Name).Append(':')
                          .Append((cur.x ^ cur.y ^ cur.z ^ cur.w).ToString("X8"));
                        s_prevStates[i] = cur;
                    }
                }
                if (sb != null)
                {
                    int slot = s_rngRingCount++ % RngRingSize;
                    s_rngRingTick[slot] = Game.Instance.RealTimeController.CurrentNetworkTick;
                    s_rngRingStreams[slot] = sb.ToString();
                }
                // Remember the last tick we were in a transition (loading / fade / cutscene). InTransition-
                // Window() then also covers the "just-after-area-load" grace, where flaps still settle.
                if (IsTransitionActive())
                    s_lastTransitionTick = Game.Instance.RealTimeController.CurrentNetworkTick;
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

        // A transition flap = a CONFIRMED randomState-only episode occurring in/just-after a transition. The
        // gate requires POSITIVE randomState confirmation (not mere absence of a contrary bucket):
        // if attribution never resolved a bucket we do NOT treat it as a flap, so the dialog shows (safe).
        // randomState alone is insufficient (RuleSystem is randomState -- the burst fork was randomState in
        // active combat); the transition window is what makes suppression safe.
        private static bool IsTransitionFlap()
        {
            return NetworkingManager.IsMultiplayer
                && s_episodeSawRandomState
                && !s_episodeSawNonRandomState
                && InTransitionWindow();
        }

        // Called by the UIDesyncHandler prefix (runs in the composite BEFORE our logger). Returns true to
        // SUPPRESS vanilla's resync dialog for a transition flap, recording that we held it + the start tick
        // so OnMismatchReported can escalate (re-show it) if it turns out not to be transient.
        internal static bool ShouldSuppressDesyncBox()
        {
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return false;
                // The vanilla dialog fires INSIDE HandleActorsState; our OnMismatchReported postfix that
                // records the bucket runs AFTER it. So we cannot trust s_episodeSawRandomState to reflect THIS
                // tick yet -- re-infer the current bucket right here so the decision at the first serious
                // prompt (the only one that matters) is self-sufficient and not a stale/cleared flag.
                RefreshEpisodeBucketNow();
                if (!IsTransitionFlap())
                    return false;
                s_episodeBoxSuppressed = true;
                s_episodeStartTick = Game.Instance.RealTimeController.CurrentNetworkTick;
                return true;
            }
            catch (Exception)
            {
                return false;   // any doubt -> let the vanilla dialog show
            }
        }

        // Clear per-episode transition-flap bookkeeping. Called from the POTENTIAL handler (once, at the true
        // start of a divergence stretch) so the flags accumulate cleanly through potential->serious.
        private static void ResetEpisodeFlags()
        {
            s_episodeSawRandomState = false;
            s_episodeSawNonRandomState = false;
            s_episodeBoxSuppressed = false;
            s_episodeEscalated = false;
        }

        // Infer the CURRENT divergence bucket from live peer hashes and fold it into the episode flags -- the
        // same flag-setting OnMismatchReported does, but on demand, because the dialog fires before that postfix.
        private static void RefreshEpisodeBucketNow()
        {
            try
            {
                var sdc = Game.Instance.SynchronizedDataController;
                var players = (sdc != null && sdc.SynchronizedData != null) ? sdc.SynchronizedData.Players : null;
                if (players == null)
                    return;
                InferBucket(players);   // sets s_lastBucketName
                if (s_lastBucketName == "randomState")
                    s_episodeSawRandomState = true;
                else if (s_lastBucketName != null)
                    s_episodeSawNonRandomState = true;
            }
            catch (Exception)
            {
            }
        }

        // Re-show the exact vanilla desync dialog on escalation. UIDesyncHandler.RaiseDesync ignores both its
        // args (it just shows a static message box), so re-invoking with defaults is safe and future-proof.
        private static void ShowVanillaDesyncBox()
        {
            try
            {
                new UIDesyncHandler().RaiseDesync(default(HashableState), default(DesyncMeta));
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.Log("[DesyncWatch][ERR] re-show desync dialog: " + e.Message);
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
                MultiplayerStabilityMain.Log(sb.ToString());

                // Track this episode's buckets: POSITIVELY confirmed randomState (the gate to suppress), and
                // any non-randomState bucket (which means it is NOT a pure transition flap).
                if (s_lastBucketName == "randomState")
                    s_episodeSawRandomState = true;
                else if (s_lastBucketName != null)
                    s_episodeSawNonRandomState = true;

                // If we HELD vanilla's resync dialog as a transition flap, escalate once it proves it was NOT
                // transient -- it graduated to another state bucket, or it is still diverged well past the
                // transition (~10s). Transient flaps recover (ReArmOnRecovery) well before this and
                // are reset, so they never reach here. On escalation we SHOW the same dialog we held, so the
                // player still gets the real prompt (UIDesyncHandler ignores its args, so re-invoking is safe).
                if (s_episodeBoxSuppressed && !s_episodeEscalated)
                {
                    bool graduated = s_episodeSawNonRandomState;
                    bool persisted = tick - s_episodeStartTick > PersistTicks && !InTransitionWindow();
                    if (graduated || persisted)
                    {
                        s_episodeEscalated = true;
                        MultiplayerStabilityMain.Log("[DesyncWatch] Transition flap did NOT clear ("
                            + (graduated ? "graduated to a non-randomState bucket" : "still diverged past the transition")
                            + ") -- showing the resync dialog now.");
                        ShowVanillaDesyncBox();
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static string InferBucket(List<PlayerCommands<SynchronizedData>> players)
        {
            int n = Math.Min(s_hashRingCount, HashRingSize);
            foreach (var pc in players)
            {
                foreach (var sd in pc.Commands)
                {
                    if (sd.IsEmpty)
                        continue;
                    for (int i = 0; i < n; i++)
                    {
                        if (s_hashRingHash[i] != sd.stateHash)
                            continue;
                        int senderTick = s_hashRingTick[i];
                        s_lastBucketName = BucketNames[((senderTick % 5) + 5) % 5];
                        return "bucket=" + s_lastBucketName
                            + " (senderTick " + senderTick + ", inferred)";
                    }
                }
            }
            s_lastBucketName = null;   // unknown -- treat conservatively (does not count as randomState-only)
            return "bucket=? (no local hash match)";
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
                AccessTools.PropertySetter(typeof(BaseDesyncDetectionStrategy), "WasDesync")
                    ?.Invoke(strat, new object[] { false });
                AccessTools.Field(strat.GetType(), "m_DesyncTickFirst")?.SetValue(strat, -32768);
                AccessTools.Field(strat.GetType(), "m_DesyncTickLast")?.SetValue(strat, -32768);
                MultiplayerStabilityMain.Log("[DesyncWatch] Desync episode recovered (hashes matched ~5s); notifications re-armed.");
            }
            // NOTE: episode-flag reset is NOT done here. HasDesync only turns true at the serious threshold
            // (SlidingWindow: 41 < tick - m_DesyncTickFirst), so a rising-edge reset here fires on the serious
            // tick and would wipe the suppress flag the dialog prefix just set. The reset lives in the POTENTIAL
            // handler instead (fires once at the true divergence-stretch start, ~41 ticks earlier).
            s_hadDesync = has;
        }

        private static void DumpRngRing(int aroundTick)
        {
            int n = Math.Min(s_rngRingCount, RngRingSize);
            if (n == 0)
            {
                MultiplayerStabilityMain.Log("[DesyncWatch] rng trace: (empty)");
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
            MultiplayerStabilityMain.Log(sb.ToString());
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
            MultiplayerStabilityMain.Log(sb.ToString());
        }
    }

    [HarmonyPatch]
    internal static class SyncStateCheckerController_Tick_RngTrace_Patch
    {
        private static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(typeof(SyncStateCheckerController), "Kingmaker.Controllers.Interfaces.IControllerTick.Tick");
        private static void Prefix() => DesyncWatch.OnSimTick();
    }

    // The ACTUAL player-facing resync dialog. Vanilla's serious-desync handler list contains UIDesyncHandler,
    // which shows ShowMessageBox(DesyncWasDetected) regardless of our appended logger. Skip it ONLY for a
    // confirmed transition flap (randomState-only, in a loading/cutscene window); OnMismatchReported re-shows
    // it if the episode escalates. Returns true (show) on anything unexpected -- never hide a real desync.
    [HarmonyPatch(typeof(UIDesyncHandler), nameof(UIDesyncHandler.RaiseDesync))]
    internal static class UIDesyncHandler_RaiseDesync_SuppressFlap_Patch
    {
        private static bool Prefix() => !DesyncWatch.ShouldSuppressDesyncBox();
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
            // Only a first-time attach (empty UniqueId) actually draws GlobalUuid; re-attach/load does not.
            __state = NetworkingManager.IsMultiplayer && string.IsNullOrEmpty(__instance.UniqueId);
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
                        + (ownerUnit.IsPreviewUnit ? "[PREVIEW]" : "");
                else
                    who = owner != null ? "@" + owner.GetType().Name : "";
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
}

# Changelog (curated)

Versions below are milestones; point releases between them were review/build iterations. Every prevention
fix was driven by a two-sided log capture or a source-verified audit finding; file headers carry the detail.

## 0.8.16
- Action-bar spam fix extended to the per-player room events (`HandlePlayerEnteredRoom`/`LeftRoom`) — the
  residual ~425-exception storm on host disconnect in the 0.8.14 three-player capture.
- Same capture archived as the reference *clean* three-player session: full mod parity, zero desyncs, both
  accelerated transfers succeeded (including a reconnect transfer delivered and accepted by the game); the
  session ended on a host-side Photon `ClientTimeout` — infrastructure, not simulation.

## 0.8.15 — hardening review round (current)
- Withdrew two overconfident mechanisms after external review: the camera-frustum substitution in the AI
  turn-speed fix (frustum tests local renderer bounds — a proxy, not deterministic; MP is now always-1×) and
  the global deterministic dialogue-cue replacement (it changed real narrative selection; the semantic
  preview-getter guard alone covers the proven leak).
- Corpse reveal flag now uses a genuinely synchronized policy (finally-dead → revealed).
- Documentation: three-part peer-compatibility taxonomy replaces per-component asymmetry claims.

## 0.8.11–0.8.14 — tester-cohort fixes
- Idle-animation RNG rerouted off the hashed `Animation3` stream to the engine's own non-hashed
  `AnimationIdle` stream (the long-standing "transition flap" desync class, root-caused at last).
- Init hardened to per-class patch isolation after an ambiguous-overload crash disabled the whole mod
  (0.8.11 was a partial-initialization build — discarded).
- Action-bar role-event spam fix: ~18,000 exception stacks per player-leave (600 MB logs) eliminated.

## 0.8.7–0.8.10 — dialogue RNG family
- Dialogue answer-preview getters no longer advance the hashed `DialogSystem` stream at view time
  (mid-conversation `randomState` forks, proven by two- and three-machine captures).

## 0.8.0–0.8.6 — the Channel-B batch
- Systematic audit of client-local view state reaching simulation: fog-gated 16× AI time-scale and 8×
  movement speed, fog-gated awareness rolls, ricochet candidate filtering, physics-order nondeterminism
  (same RNG draw resolving to different victims), combat-capable sleep census, corpse-flag and fader-wake
  writes, local slow-mo. Census rebuilt as a single-write replacing prefix after a view-toggle churn
  regression (the postfix double-write storm).
- Projectile target geometry made entity-derived in MP (view-bone transforms fed ricochet legs, push
  directions, and the space-combat hull-point path).

## 0.7.x — ghosts, transfers, transition policy
- UI preview-unit "ghosts" excluded from uuid minting, aura membership, and global rulebook subscriptions
  (burst-attack `RuleSystem` forks).
- Steam P2P save transfer (~8× speedup) with adaptive rate control and transparent Photon fallback.
- Proactive out-of-tick hashed-RNG leak detector.
- Transition-window desync dialogs suppressed for confirmed transient flaps, re-shown on escalation.

## 0.5.x–0.6.x — the awake-set era
- Loading-barrier sequencing (client stuck at 100% while host is in the area).
- Deterministic awake census (death-timing forks, ability target counts, combat-join membership) with
  distance-based valves replacing camera/fog verdicts; starships and dying units always awake.
- Fog-gate transpiler: mechanics decisions no longer read client-local fog.

## 0.3.x–0.4.x — foundations
- DesyncWatch instrumentation (episodes, buckets, RNG fingerprint rings, uuid mint rings).
- Weather VFX RNG leak fix (the first root-caused desync class).
- Projectile aim-bone RNG fix (burst-fire desyncs).

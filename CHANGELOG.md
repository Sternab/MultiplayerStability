# Changelog

Versions below are milestones; point releases between them were review/build iterations. Every prevention
fix was based on a two-sided log capture or a source-verified finding. Source-file headers contain
implementation details.

## 0.8.29-0.8.32
- Two paired diagnostics for the charge/parry same-tile desyncs (tester: "my char and the enemy stood on the
  same tile"; `ChargeBuff` 12-13 ticks before both first mismatches): charge-path resolutions logged with
  cache source. The partial-cache lookup ignores the target entity and cuts a possibly preview-polluted
  cached path at the destination node. Dark Heresy removed this lookup. The Tactician diagnostic logs each
  momentum event with its delta and post-event remainder; the accumulator is omitted from its part hash, so
  divergence remains invisible until a one-sided 100-crossing. Supporting evidence for the charge fix was a
  one-peer partial-cache hit ending on the target's node.
- 0.8.30: disabled partial charge-path cache reuse in MP. Exact, target-checked hits remain enabled,
  unmatched paths recompute, and solo remains unchanged. This matches Dark Heresy's newer implementation,
  which removed the partial lookup. The source diagnostic remains enabled as the fix validator.
  Also corrected: DashDeliveryFix's comment claiming delivery-tick skew "self-heals": count-equal streams
  re-align, but threshold-crossing accumulators (the Tactician remainder) can latch a skew permanently.
- 0.8.31 hardening: moved the `[ChargeFix]` activation log out of the fail-open path. Activation logs
  must remain outside fail-open `try` blocks. The file header states the
  fix status and exact-parity requirement, and documents the validator's limits. The prefix and
  postfix share one patch class, so verification needs all three signals (no init error, `[ChargeFix] Active`,
  no `partial-cache` lines), not silence alone. Updated `MOD-PLAN.md` to match the implementation.
- 0.8.32: the activation latch sets only after a successful log, so a transient logger failure retries
  later. Split the trap component's compatibility label between its subset-safe diagnostic and
  exact-parity containment.

## 0.8.26-0.8.28
- Added trap/pause containment based on a 4.5-hour capture: 72-vs-10 trap NREs, 514-vs-107
  residual command exceptions, forks isolated to touched units, zero RNG differences; three trap storms
  immediately preceded room disconnects. In MP, `ForceRotateToDesired`'s paused visible-unit IK reset is
  null-safe: the shared NRE was aborting the command batch mid-tick with per-peer retry residue. Sim
  orientation write, vanilla view rotation, and every unrelated exception preserved; solo untouched.
  Diagnostic breadcrumbs and the finalizer remain enabled to validate the containment.
- 0.8.27 hardening: containment logging is best-effort (a logger throw can no longer re-enable
  the vanilla NRE); reflection drift (failed member lookup after a game update) now latches back to vanilla
  instead of masquerading as the known null-IK defect; the orientation FieldRef resolves in a guarded
  `Prepare()` (patch declines on rename); and a real `ResetPosition` failure surfaces once, unwrapped,
  with no vanilla rerun of a non-idempotent reset. Wording corrected: the fix prevents the NRE from aborting
  the batch. It does not claim batches are otherwise identical.
- 0.8.28: moved the one-time activation log into the same no-throw helper. It was the last log call inside
  the fail-open try and could re-enable the contained NRE if logging failed.
- Open from the same capture (diagnostics queued next): Tactician `MomentumThisCombat` hidden-accumulator
  divergence (omitted from its part hash: same base-only hash mistake found in `MomentumReachedTrigger`,
  `HunterDodge`, `ChangeVeilDamage`; bounded hash-audit queued) and the area-effect candidate census filled
  from local Unity trigger callbacks (different candidate *sets*: beyond FogGateFix's predicate repair).

## 0.8.24-0.8.25
- Augmentation-screen bark containment (capture 0.8.23 `player`-bucket fork): the client-local augmentation
  UI picked a bark with `UnityEngine.Random` and its handler wrote hashed `Player.PlayedBanters` on one peer.
  The fix is caller-scoped: a flag brackets the `AugmentationsVM` constructor; `HandleBarkBanter` skips while set in MP.
  Simulation-side banter raisers (etude `ShowBanter`, system-map objects) are unchanged.
- Preview-copy scope completed (same capture, `GlobalUuid` count fork): vanilla's `DisableStatefulRandomContext`
  closes before `CopyItems`, so preview *items* minted hashed uuids; in MP the whole `Copy(..., preview: true)`
  now holds the context.
- 0.8.25 hardening: bark ctor bracket is a nesting-safe depth counter; every
  `DisableStatefulRandomContext` dispose failure now reports an error (a stuck context would contaminate every
  subsequent hashed draw); PreviewGhostFix, the component roster, and the plan now describe the
  three-patch implementation.
- Same capture also validated: all three save transfers fed-to-game, TrapDiag produced 27 matching
  records (no exceptions), WeatherDiag matched through both forks, census counts matched, and the flap policy prompted
  on both real forks. Open: the Eogann combat fork (all streams/creations matched: needs per-entity hash
  decomposition, queued) and the `senderTick` inference spread (+/-40 ticks: reinforces the 0.9(e) tick-identity
  work).

## 0.8.20-0.8.23
- Dialogue guard C: the third confirmed view-time dialogue caller (`HasNextUnselectedAnswers`, the answer
  tree inspection behind the Solomorne-dialogue fork) now holds the RNG-divert context in MP: the same
  semantic caller-wrap shape as the preview getters; LeakDetector reported these draws before
  the fork.
- Log-only trap/pause diagnostic: trap auto-pause leads `ForceRotateToDesired` to write sim orientation and
  then touch client-local view/IK state; a one-machine exception there skips command bookkeeping and forks
  `sceneEntities` (party members only, RNG identical: a longstanding vanilla defect also visible in 0.6.4
  logs). Instruments the paused window and all exceptions; containment follows the evidence.
- 0.8.21 hardening: breadcrumb budget made per-pause-episode (a session-lifetime cap would have erased
  the successful peer's comparison evidence. Both 0.8.19 peers threw, so comparison uses keyed
  `(networkTick, unit)` sets, throw-vs-success at matching keys, not mere exception presence), and
  `ViewTransform` nullity added to the state dumps (it is dereferenced before the IK chain).
- 0.8.22-0.8.23 hardening: moved the episode boundary to the *accepted* transition
  (`HandleGameModeChanged`, `newMode == Pause`: `StartMode` is only a request that can be rejected or
  deferred, so a request-side reset could misalign across peers), with tick-regression as the save/reload
  fallback; and every paused-window line carries a per-tick per-unit invocation ordinal from a dictionary
  (interleaved `A,B,A` batching cannot collide; unpaused calls never perturb the key space), making the
  two-sided comparison a unique-keyed `(tick, unit, seq)` diff: upstream call-count divergence cannot
  masquerade as throw-versus-success.

## 0.8.18-0.8.19
- Log-only diagnostic for a newly captured desync class: the combat-exit weather path (visual
  `IsProfileOverriden` flag steering hashed-Weather draws into hashed player fields). Logs the full gating
  input set: both controllers' `TargetInclemency`, veil counter, profile-override flag,
  `CurrentWeatherEffect`, and each `SetNewInclemency` call with weather-vs-wind attribution and its
  bracketing pre->post stream fingerprints. This component is diagnostic only.
- 0.8.19 hardening: `CurrentWeatherEffect` is a public field (the property lookup silently returned null), the
  two `TargetInclemency` gates were unlogged, and draw-site calls were unattributed: all corrected before
  first distribution; 0.8.18 never shipped.
- Field validation recorded from the same capture: the action-bar teardown guard held (zero exceptions on a
  2->1 departure), ghost protections contained a large preview build, the transition-flap policy did not
  suppress the real fork, and a Photon `ServerTimeout` transfer retry delivered and applied 15.4 MB.

## 0.8.16-0.8.17
- Completed the action-bar spam fix against the 0.8.14 capture's 425 residual exception
  stacks: the per-player room events (`HandlePlayerEnteredRoom`/`LeftRoom`) gained the unitless-slot guard
  (~155 stacks), and the `IsMultiplayer` gate was removed from both guards: instantaneous `PlayerCount > 1`
  goes false *before* 2->1 departure callbacks, so the gate had been disabling the guard during the
  highest-volume window (~270 stacks). The filter is ungated because its invariant is valid in every context,
  including teardown and rare non-departure raisers, so no player-count test is required.
- Archived the same capture as the reference three-player session: full mod parity, zero desyncs, both
  accelerated transfers succeeded (including a reconnect transfer delivered and accepted by the game); the
  session ended on a host-side Photon `ClientTimeout`: infrastructure, not simulation.

## 0.8.15: Hardening
- Withdrew two unsupported mechanisms after source and implementation review: the camera-frustum substitution in the AI
  turn-speed fix (frustum tests local renderer bounds: a proxy, not deterministic; MP is now always-1x) and
  the global deterministic dialogue-cue replacement (it changed real narrative selection; the semantic
  preview-getter guard alone covers the proven leak).
- Corpse reveal flag now uses synchronized state (finally-dead -> revealed).
- Documentation: three-part peer-compatibility taxonomy replaces per-component asymmetry claims.

## 0.8.11-0.8.14: Tester fixes
- Idle-animation RNG rerouted off the hashed `Animation3` stream to the engine's own non-hashed
  `AnimationIdle` stream (the source of the long-standing "transition flap" desync class).
- Init hardened to per-class patch isolation after an ambiguous-overload crash disabled the whole mod
  (0.8.11 was a partial-initialization build: discarded).
- Action-bar role-event spam fix: ~18,000 exception stacks per player-leave (600 MB logs) eliminated.

## 0.8.7-0.8.10: Dialogue RNG
- Dialogue answer-preview getters no longer advance the hashed `DialogSystem` stream at view time
  (mid-conversation `randomState` forks, proven by two- and three-machine captures).

## 0.8.0-0.8.6: Client-local state fixes
- Systematic audit of client-local view state reaching simulation: fog-gated 16x AI time-scale and 8x
  movement speed, fog-gated awareness rolls, ricochet candidate filtering, physics-order nondeterminism
  (same RNG draw resolving to different victims), combat-capable sleep census, corpse-flag and fader-wake
  writes, local slow-mo. Census rebuilt as a single-write replacing prefix after a view-toggle churn
  regression (the postfix double-write storm).
- Projectile target geometry made entity-derived in MP (view-bone transforms fed ricochet legs, push
  directions, and the space-combat hull-point path).

## 0.7.x: Preview units, transfers, and transition policy
- UI preview-unit "ghosts" excluded from uuid minting, aura membership, and global rulebook subscriptions
  (burst-attack `RuleSystem` forks).
- Steam P2P save transfer (~8x speedup) with adaptive rate control and transparent Photon fallback.
- Out-of-tick hashed-RNG leak detector.
- Transition-window desync dialogs suppressed for confirmed transient flaps, re-shown on escalation.

## 0.5.x-0.6.x: Awake census
- Loading-barrier sequencing (client stuck at 100% while host is in the area).
- Deterministic awake census (death-timing forks, ability target counts, combat-join membership) with
  distance-based valves replacing camera/fog verdicts; starships and dying units always awake.
- Fog-gate transpiler: mechanics decisions no longer read client-local fog.

## 0.3.x-0.4.x: Initial diagnostics and fixes
- DesyncWatch instrumentation (episodes, buckets, RNG fingerprint rings, uuid mint rings).
- Weather VFX RNG leak fix (the first confirmed desync source).
- Projectile aim-bone RNG fix (burst-fire desyncs).

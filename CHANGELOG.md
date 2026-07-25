# Changelog

This file records public milestones. Point releases that only corrected builds, diagnostics, or
documentation are grouped with the behavior they support.

## 0.8.29 to 0.8.32

- Added charge-path source diagnostics for the reported charge, attack, parry, and same-tile
  desync.
- Disabled target-blind partial charge-path cache reuse in multiplayer. Exact target-checked cache
  hits remain enabled; other paths are recomputed.
- Added Tactician momentum diagnostics for the hash-omitted `MomentumThisCombat` remainder.
- Hardened charge-fix activation reporting and documented the three required health signals:
  successful patch startup, `[ChargeFix] Active`, and no multiplayer `partial-cache` result.

## 0.8.26 to 0.8.28

- Added null-safe containment for the paused trap-detection IK reset that could throw after a
  simulation orientation write and abort command processing.
- Kept the trap diagnostic active as a validator.
- Separated reflection drift, known null state, logging failure, and real reset exceptions so each
  follows an explicit failure path.

## 0.8.20 to 0.8.25

- Added a third dialogue inspection guard for `HasNextUnselectedAnswers`.
- Added keyed trap and pause diagnostics with per-pause budgets and per-tick invocation ordinals.
- Prevented the client-only augmentation screen bark from writing hashed played-banter state.
- Extended preview-copy RNG isolation through item copying and UUID creation.
- Added explicit errors for failed disposal of non-stateful RNG contexts.

## 0.8.18 to 0.8.19

- Added a log-only diagnostic for combat-exit weather selection.
- Recorded both weather controllers, target inclemency gates, veil state, profile override state,
  current effect, and pre/post Weather stream fingerprints.

## 0.8.16 to 0.8.17

- Guarded action-bar role and room callbacks against unitless player slots.
- Removed the instantaneous multiplayer gate from teardown callbacks because player count has
  already changed when a 2-to-1 departure is reported.

## 0.8.15

- Removed the camera-frustum substitute from AI turn fast-forward. Multiplayer AI turns now stay at
  1x because frustum tests depend on local renderer bounds.
- Removed a global deterministic dialogue-cue replacement that changed real narrative selection.
- Changed finally-dead corpse reveal state to a synchronized policy.

## 0.8.11 to 0.8.14

- Routed idle-animation variety away from the hashed `Animation3` stream.
- Isolated Harmony initialization per patch class so one failed target does not disable the whole
  mod.
- Added the first action-bar guard for high-volume disconnect exception logging.

## 0.8.7 to 0.8.10

- Prevented dialogue answer-preview getters from advancing the hashed `DialogSystem` stream during
  UI refresh.
- Added transition-flap dialog policy based on live state attribution and accepted game-mode
  transitions.

## 0.8.0 to 0.8.6

- Audited client-local view state reaching simulation.
- Expanded fog-gate fixes to movement, awareness, ricochet, and line-of-sight mechanics.
- Replaced the multiplayer awake census with a single-write deterministic pass.
- Added deterministic range-query ordering.
- Removed local visibility and pause-bind writes from hashed game time.
- Replaced view-bone projectile geometry with entity-derived positions.

## 0.7.x

- Isolated UI preview units from hashed UUID creation, aura membership, fact attachment, and global
  rulebook subscriptions.
- Added Steam Networking Messages save transfer with Photon control and fallback.
- Added the out-of-tick hashed RNG leak detector.
- Added transition-window desync diagnostics and delayed warnings for confirmed transient flaps.

## 0.5.x to 0.6.x

- Added sequenced loading barriers.
- Added the first deterministic awake census and distance-based scene-loop sleep policy.
- Added the original fog-gate mechanics patch.

## 0.3.x to 0.4.x

- Added DesyncWatch episode, RNG, bucket, and entity diagnostics.
- Fixed render-loop weather draws reaching the hashed Weather stream.
- Fixed view-dependent projectile aim-bone draws that caused burst-fire desyncs.

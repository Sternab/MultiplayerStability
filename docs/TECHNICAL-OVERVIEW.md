# MultiplayerStability Technical Overview

## Scope

MultiplayerStability is an OwlcatModification that instruments Rogue Trader's lockstep simulation
and applies targeted Harmony patches for confirmed desync causes. Analysis of two-player and
three-player captures found repeated cases where client-local state affected synchronized state.
**v0.8.32 is the current release and was built from tag `v0.8.32`.** That build has not completed
post-fix field validation.
Per-component validation status is tracked individually in `PATCH-CATALOG.md`;
`RELEASE-0.8.32.md` holds the exact artifact identity.

## Summary

Across the captures reviewed on game build `1.6.1.514`, the recurring divergence pattern is
**client-local input (fog, camera, render visibility, view objects/bones, UI refresh timing, Unity
physics and trigger callbacks, and cache pollution from aiming previews) entering hashed simulation
state**. Each prevention fix either removes one of these inputs or derives the decision from
synchronized state. Open items that do not yet fit this pattern are listed in
`KNOWN-LIMITATIONS.md`.

## Reading order

1. **`PATCH-CATALOG.md`:** all 23 components, including target methods, observed engine behavior,
   intervention, validation status, and a source note locating the relevant engine path.
2. **`EVIDENCE-MATRIX.md`:** symptom, mechanism, fix, and validation traceability with capture
   references and first-divergent-tick data.
3. **`KNOWN-LIMITATIONS.md`:** unproven behavior, untested configurations, and open
   investigations (two instrumented-but-unfixed classes ship in this build as log-only diagnostics).

## Categories at a glance

- **Engine code paths:** behavior originates in engine code; the mod applies a downstream patch.
  Weather RNG (2 classes), projectile RNG and geometry, dialogue UI RNG (3 call sites), idle-animation
  RNG stream selection, awake-census determinism, fog-gated mechanics reads (6 sites), local
  time-scale writers, physics-order nondeterminism, preview-unit isolation (3 mechanisms),
  charge-path partial cache, paused-command IK NRE, bark/`PlayedBanters` UI write, preview-copy RNG
  scope.
- **Diagnostic tooling:** out-of-tick hashed-RNG leak detector, per-bucket desync attribution,
  RNG/entity fingerprint rings, and four active scoped diagnostics.
- **Mod-only infrastructure:** Steam P2P save-transfer side channel,
  Photon ack-pump, sequenced loading barriers, per-class patch isolation.

## Design constraints

- **No automatic resync:** recovery stays under player control.
- **Best-effort fail-open:** patching is isolated per class, and runtime guards fall back to the
  vanilla path at their own site; a component spanning several classes or targets can be left
  partially active (details and residual risks in `KNOWN-LIMITATIONS.md`).
- **Solo-safe:** multiplayer-gated behavior; solo is vanilla except for two documented cases.
- **Validation policy:** a fix is only called *field validated* after a post-fix two-sided
  capture; statuses in the catalog use a strict vocabulary.

## Reproduction and verification

`REPRODUCING.md` has per-issue procedures (including the charge/parry same-tile scenario and the
diagnostic log contracts). `TESTING.md` describes current verification and the automated-test plan.
`BUILDING.md` is a build guide for Unity `6000.0.64f1` and the hashed reference
assemblies.

## Planned work

`ROADMAP-0.9.md` defines five hardening changes: session-latched peer compatibility, P2P wire
framing, transfer ACK/NACK, barrier retry/abort, and conservative inference. The series contains no
new gameplay fixes.

## Contact

Author: **Sternab**. Repository commits and package checksums in
`RELEASE-0.8.32.md` identify the reviewed source and release artifact.

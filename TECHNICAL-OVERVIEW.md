# MultiplayerStability — Technical Overview

One page. Everything else links from here.

## What this is

A field-driven investigation of Rogue Trader co-op desyncs, packaged as an OwlcatModification
(Harmony). Across instrumented two- and three-player sessions it identified, and where safely
possible fixed, systematic violations of the lockstep contract: client-local state reaching the
synchronized simulation. **v0.8.32 is the review artifact, built from the tagged source**; this
exact build has not completed post-fix field validation.
Per-component validation status is tracked individually in `PATCH-CATALOG.md`;
`HANDOFF-MANIFEST.md` holds the exact artifact identity.

## The one-sentence thesis

Across the captures reviewed on game build `1.6.1.514`, the recurring divergence pattern is
**client-local input (fog, camera, render visibility, view objects/bones, UI refresh timing, Unity
physics/trigger callbacks, cache pollution from aiming previews) entering hashed simulation state** —
and each prevention fix here either severs one such path or makes the affected decision derive from
synchronized state. Open items that do not yet fit this pattern are listed in
`KNOWN-LIMITATIONS.md`.

## Reading order

1. **`PATCH-CATALOG.md`** — all 23 components: target methods, observed vanilla defect, the mod's
   intervention, validation status, and a root-cause note locating each defect's origin.
2. **`EVIDENCE-MATRIX.md`** — symptom → mechanism → fix → validation traceability with capture
   references and first-divergent-tick data.
3. **`KNOWN-LIMITATIONS.md`** — what is *not* proven, untested configurations, and open
   investigations (two instrumented-but-unfixed classes ship in this build as log-only diagnostics).

## Categories at a glance

- **Engine-origin defect classes** (root cause in engine code; the mod intervenes downstream):
  weather RNG (2 classes), projectile RNG + geometry, dialogue UI RNG (3 call sites), idle-animation
  RNG stream selection, awake-census determinism, fog-gated mechanics reads (6 sites), local
  time-scale writers, physics-order nondeterminism, preview-unit isolation (3 mechanisms),
  charge-path partial cache, paused-command IK NRE, bark/`PlayedBanters` UI write, preview-copy RNG
  scope.
- **Diagnostic tooling**: out-of-tick hashed-RNG leak detector, per-bucket desync attribution +
  RNG/entity fingerprint rings, and four armed scoped diagnostics.
- **Mod-only infrastructure** (specific to running as a mod): Steam P2P save-transfer side channel,
  Photon ack-pump, sequenced loading barriers, per-class patch isolation.

## Ground rules this project follows

- **No auto-resync** — recovery stays the player's choice; the mod diagnoses and prevents.
- **Best-effort fail-open** — patching is isolated per class, and runtime guards fall back to the
  vanilla path at their own site; a component spanning several classes or targets can be left
  partially active (details and residual risks in `KNOWN-LIMITATIONS.md`).
- **Solo-safe** — multiplayer-gated behavior; solo is vanilla (two narrow, documented exceptions).
- **Evidence discipline** — a fix is only called *field validated* after a post-fix two-sided
  capture; statuses in the catalog use a strict vocabulary.

## Reproduction & verification

`REPRODUCING.md` has per-issue procedures (including the charge/parry same-tile scenario and the
diagnostic log contracts). `TESTING.md` describes current verification and the automated-test plan.
`BUILDING.md` is a clean-room build guide against Unity `6000.0.64f1` and the hashed reference
assemblies.

## What comes next (not in this build)

`ROADMAP-0.9.md` — a frozen five-part hardening series (session-latched peer compatibility, P2P wire
framing, transfer ACK/NACK, barrier retry/abort, inference conservatism). No new gameplay fixes ride
in that series.

## Contact

Author: **Sternab** (mod author / field-test lead). Repository commits and package checksums in
`HANDOFF-MANIFEST.md` are the canonical identity for any question about "which build."

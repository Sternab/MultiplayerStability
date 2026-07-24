# MultiplayerStability

**Experimental** co-op stability mod for *Warhammer 40,000: Rogue Trader* — an active investigation into the
game's deterministic-lockstep multiplayer, packaged as an OwlcatModification. It makes desyncs **diagnosable**
(tick, state bucket, RNG-stream and entity attribution in the log), removes **root causes** where a Harmony
mod safely can, and makes co-op save transfers roughly **8× faster** over a direct Steam channel.

> **Status: research build.** This is presented as an experimental lockstep investigation seeking technical
> review, not a finished stability product. It has been field-tested in 2–3 player sessions by a small tester
> group. Expect rough edges; read the compatibility rules below before mixing versions.

## What it does

- **Diagnosis** — every desync episode is logged with the diverged state bucket, per-tick RNG-stream
  fingerprints, entity/fact creation rings, and per-entity state hashes, so two machines' logs can be diffed
  to the diverging subsystem — and, for every class fixed so far, to the exact call site (one instrumented
  single-entity fork remains open; see `EVIDENCE-MATRIX.md`). A proactive leak detector flags out-of-tick hashed RNG draws on a single machine, no
  desync required.
- **Prevention** — root-cause fixes for confirmed desync classes: view-timed/hashed RNG leaks (weather,
  projectiles, dialogue previews, idle animations), client-local state reaching mechanics (fog gates, awake
  census, view-bone geometry, local time-scales, physics-order nondeterminism), and UI preview ghosts
  polluting simulation streams.
- **Transfer** — co-op save transfer over Steam networking (ICE direct / SDR relay) with adaptive rate
  control, plus a Photon ack-pump; measured ~0.22 MB/s → ~2 MB/s. Falls back to vanilla transparently.
- **Never auto-resyncs.** Recovery stays the player's choice; the mod only diagnoses, prevents, and informs.

## Compatibility rules (important)

| Category | Components | Mixed-install behavior |
|---|---|---|
| Subset-safe | Diagnostics, UI fixes, transfer ack-pump | Fine on any subset of machines |
| Negotiated protocol | Steam P2P transfer, Sequenced Locks, window boost | Self-disable unless every peer has the mod |
| **Exact parity required** | Every RNG/simulation-changing fix | **Run the identical version on every machine** — mixed installs range from useless to actively desync-causing |

A session-latched compatibility gate (auto-fallback to vanilla on version mismatch) is planned for 0.9.
Until then: same version, every machine, every session.

Requires the Steam edition for fast transfers. On GOG the Steam transfer self-disables (vanilla transfer
speed); the remaining components are expected to work by design but are **not field-tested on GOG**. Coexists with UnityModManager mods (ToyBox, MicroPatches) — but remember those are subject
to the same lockstep rules; mismatched gameplay mods across peers cause their own desyncs.

## Install

1. Both (all) players install the same release into
   `%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Modifications\MultiplayerStability`.
2. Enable it in the in-game mod manager.
3. Verify in `GameLogFull.txt` after launch: `[MPStability] [Init] Patches applied (N classes)` with no
   `FAILED`, and no `[Init][ERR]` lines.

## Documentation

Start with `TECHNICAL-OVERVIEW.md` — the one-page engineering overview.

- `PATCH-CATALOG.md` — canonical per-component inventory: every patch class, its targets, mechanism,
  peer-compatibility category, and exact evidence status.
- `EVIDENCE-MATRIX.md` — claim-by-claim mapping to the field captures and source analysis behind each fix.
- `KNOWN-LIMITATIONS.md` — the honest boundary: what is not fixed, not proven, or out of scope.
- `REPRODUCING.md` — boot health check, per-component verification procedures, two-sided capture protocol.
- `TESTING.md` — verification that exists today and the automated-test plan.
- `BUILDING.md` — reproducible build from source (Unity + the Owlcat modification template).
- `COMPATIBILITY.md` — tested vs design-only status per game build, player count, and platform.
- `ROADMAP-0.9.md` — the frozen 0.9 plan (session compatibility latch first).
- `HANDOFF-MANIFEST.md` — exact artifact identity: package/DLL/reference-assembly SHA-256 hashes.
- `CHANGELOG.md` — curated release history with the investigation findings behind each fix.

Each source file's header carries that component's detailed rationale; `PATCH-CATALOG.md` is the index
over them. The underlying investigation (engine analysis, capture forensics, hazard registry) lives in a
separate research repository; the capture evidence each claim rests on is cited in `EVIDENCE-MATRIX.md`.

## License

MIT — see `LICENSE`.

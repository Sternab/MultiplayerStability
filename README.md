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
  to the exact cause. A proactive leak detector flags out-of-tick hashed RNG draws on a single machine, no
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

Requires the Steam edition for fast transfers (GOG installs fall back to vanilla transfer speed but are
otherwise fine). Coexists with UnityModManager mods (ToyBox, MicroPatches) — but remember those are subject
to the same lockstep rules; mismatched gameplay mods across peers cause their own desyncs.

## Install

1. Both (all) players install the same release into
   `%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Modifications\MultiplayerStability`.
2. Enable it in the in-game mod manager.
3. Verify in `GameLogFull.txt` after launch: `[MPStability] [Init] Patches applied (N classes)` with no
   `FAILED`, and no `[Init][ERR]` lines.

## Documentation

- `CHANGELOG.md` — curated release history with the investigation findings behind each fix.
- `COMPATIBILITY.md` — tested game builds, player counts, platform notes.
- `BUILDING.md` — how to build from source (Unity + the Owlcat modification template).

The underlying investigation (engine analysis, capture forensics, hazard registry) lives in the project's
research notes and is summarized in each source file's header — the headers are intentionally verbose; they
are the primary documentation of *why* each patch exists and what evidence supports it.

## License

MIT — see `LICENSE`.

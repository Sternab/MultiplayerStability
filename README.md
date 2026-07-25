# MultiplayerStability

Co-op stability mod for *Warhammer 40,000: Rogue Trader* — an investigation into the game's
deterministic-lockstep multiplayer, packaged as an OwlcatModification. It makes desyncs
**diagnosable** (tick, state bucket, RNG-stream and entity attribution in the log), removes **root
causes** where a Harmony mod safely can, and moves co-op save transfers onto a direct Steam channel
(~8× measured).

> **Status: research build.** This is an experimental lockstep investigation seeking technical
> review, not a finished stability product. It has been field-tested in 2–3 player sessions by a
> small tester group. Several shipped fixes are still awaiting post-fix validation — see
> [Known limitations](#known-limitations). Expect rough edges, and read the compatibility rules
> before mixing versions.

## What it does

- **Diagnosis** — every desync episode is logged with the diverged state bucket, per-tick RNG-stream
  fingerprints, entity/fact creation rings, and per-entity state hashes, so two machines' logs can be
  diffed to the diverging subsystem, and for every class fixed so far to the exact call site. A
  proactive leak detector flags out-of-tick hashed RNG draws on a single machine, no desync required.
- **Prevention** — root-cause fixes for confirmed desync classes: view-timed hashed RNG leaks
  (weather, projectiles, dialogue previews, idle animations), client-local state reaching mechanics
  (fog gates, awake census, view-bone geometry, local time-scales, physics-order nondeterminism), and
  UI preview units polluting simulation streams.
- **Transfer** — co-op save transfer over Steam networking (ICE direct / SDR relay) with adaptive
  rate control, plus a Photon ack-pump. Falls back to vanilla transparently.
- **Never auto-resyncs.** Recovery stays the player's choice; the mod diagnoses, prevents, and
  informs.

The recurring defect pattern across every capture reviewed: **client-local input entering hashed
simulation state.** [docs/TECHNICAL-OVERVIEW.md](docs/TECHNICAL-OVERVIEW.md) is the one-page version.

## Requirements and compatibility

- **Game:** Warhammer 40,000: Rogue Trader **1.6.1.514** (Steam). Other builds are unverified —
  after any game update, check the boot log and re-run `tools/check-harmony-targets.py`.
- **Exact parity:** every RNG- or simulation-changing fix requires the **identical mod version on
  every machine in the session**. Mixed installs range from ineffective to actively desync-causing.
  Diagnostics, UI-only fixes and the ack-pump are safe on any subset; the Steam transfer and
  sequenced barriers self-disable unless every peer has the mod. A session-latched compatibility gate
  is planned for 0.9. Until then: same version, every machine, every session.
- **Platform:** the fast transfer needs the Steam edition; on GOG it self-disables and everything
  else is expected to work, but that is untested.
- **Other mods:** any gameplay-affecting mod must also be installed identically on all peers —
  lockstep applies to the whole process.

Full detail in [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md).

## Install

1. Download `MultiplayerStability-0.8.32.zip` from [Releases](../../releases) — every player installs
   the **same** version.
2. Extract it into
   `%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Modifications\MultiplayerStability`
3. Enable it in the in-game mod manager.
4. Verify in `GameLogFull.txt` after launch: `[MPStability] [Init] Patches applied (45 classes)` with
   no `[Init][ERR]` lines and no `PATTERN NOT FOUND`.

Release hashes and full verification steps: [docs/RELEASE-0.8.32.md](docs/RELEASE-0.8.32.md).

## Documentation

| Document | What's in it |
|---|---|
| [TECHNICAL-OVERVIEW.md](docs/TECHNICAL-OVERVIEW.md) | One page: the defect pattern, component categories, ground rules |
| [PATCH-CATALOG.md](docs/PATCH-CATALOG.md) | Canonical inventory of all 23 components: targets, defect, intervention, evidence status |
| [EVIDENCE-MATRIX.md](docs/EVIDENCE-MATRIX.md) | Symptom → mechanism → fix → validation, per convicted issue |
| [KNOWN-LIMITATIONS.md](docs/KNOWN-LIMITATIONS.md) | What is not fixed, not proven, or out of scope |
| [REPRODUCING.md](docs/REPRODUCING.md) | Boot health check, per-component verification, two-sided capture protocol |
| [TESTING.md](docs/TESTING.md) | Verification that exists today, and the automated-test plan |
| [BUILDING.md](docs/BUILDING.md) | Building from source against the Owlcat modification template |
| [COMPATIBILITY.md](docs/COMPATIBILITY.md) | Tested vs design-only per game build, player count, platform |
| [ROADMAP-0.9.md](docs/ROADMAP-0.9.md) | The frozen 0.9 plan (session compatibility latch first) |
| [RELEASE-0.8.32.md](docs/RELEASE-0.8.32.md) | Build identity, assembly hashes, artifact verification |
| [docs/investigation/](docs/investigation/) | The engine-side research behind the fixes: lockstep architecture, hazard registry, transfer-speed analysis, capture excerpts, status-of-record plan |

Each source file's header carries its component's detailed rationale;
[docs/PATCH-CATALOG.md](docs/PATCH-CATALOG.md) is the index over them.

## Known limitations

Nine of the 23 components are **shipped but not yet field validated** — the mechanism is confirmed
from captures or engine source, but no post-fix two-sided capture has proven the fix. The charge-path
cache fix (C22) in particular still needs the charge → attack → parry scenario re-run. Two desync
classes ship instrumented but unfixed (weather combat-exit, Tactician momentum remainder), and one
single-entity fork remains entirely open.

The status vocabulary is strict and used consistently: *Field validated* means a post-fix two-sided
capture showed the class absent under conditions that previously produced it. Everything else says
so. [docs/KNOWN-LIMITATIONS.md](docs/KNOWN-LIMITATIONS.md) is the full inventory.

## Building and testing

```bash
python tools/check-harmony-targets.py <assemblies...> --reconcile Scripts
python tools/verify-comments-only.py v0.8.32..main
powershell -File tools/verify-package.ps1 -Path <package.zip>
```

`tools/check-harmony-targets.py` needs Python 3 with `dnfile==0.18.0`. Build instructions (Unity
`6000.0.64f1`, the Owlcat modification template, and the compile/package/install distinction) are in
[docs/BUILDING.md](docs/BUILDING.md).

## Contact

**@Sternab** — issues and pull requests welcome. Raw multi-machine capture logs behind the evidence
matrix are retained privately and can be shared in redacted form on request; two- and three-machine
capture sessions can be run to reproduce a catalogued desync class or validate a proposed change.

## License

MIT — see [LICENSE](LICENSE).

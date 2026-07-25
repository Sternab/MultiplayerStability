# MultiplayerStability

MultiplayerStability is an OwlcatModification for *Warhammer 40,000: Rogue Trader*. It adds
lockstep diagnostics, applies targeted fixes for confirmed desync causes, and accelerates co-op save
transfers over Steam networking. The measured transfer improvement is approximately 8x.

> **Status: research build.** This is experimental software, not a finished stability product.
> A small tester group has run it in two-player and three-player sessions. Several shipped fixes
> still require post-fix validation. Review
> [Known limitations](#known-limitations) and the version-parity requirements before use.

## What it does

- **Diagnostics:** logs each desync episode with the divergent state bucket, per-tick RNG stream
  fingerprints, entity and fact creation rings, and per-entity state hashes. Paired logs can usually
  identify the affected subsystem and, for resolved cases, the call site. A leak detector also
  reports hashed RNG draws that occur outside simulation ticks.
- **Simulation fixes:** targeted changes for confirmed desync classes, including view-timed hashed RNG leaks
  (weather, projectiles, dialogue previews, idle animations), client-local state reaching mechanics
  (fog gates, awake census, view-bone geometry, local time-scales, physics-order nondeterminism), and
  UI preview units polluting simulation streams.
- **Save transfer:** moves bulk save data to Steam Networking Messages, using ICE where available
  and Steam Datagram Relay otherwise. Photon remains the control channel and fallback path.
- **Recovery policy:** never starts a resync automatically. Recovery remains a player decision.

Most confirmed issues share one pattern: **client-local input reaches hashed simulation state.**
See [docs/TECHNICAL-OVERVIEW.md](docs/TECHNICAL-OVERVIEW.md) for the architecture summary.

## Requirements and compatibility

- **Game:** Warhammer 40,000: Rogue Trader **1.6.1.514** (Steam). Other builds are unverified.
  After any game update, check the boot log and re-run `tools/check-harmony-targets.py`.
- **Exact parity:** every RNG- or simulation-changing fix requires the **identical mod version on
  every machine in the session**. Mixed installs range from ineffective to actively desync-causing.
  Diagnostics, UI-only fixes and the ack-pump are safe on any subset; the Steam transfer and
  sequenced barriers self-disable unless every peer has the mod. A session-latched compatibility gate
  is planned for 0.9. Until then: same version, every machine, every session.
- **Platform:** the fast transfer needs the Steam edition; on GOG it self-disables and everything
  else is expected to work, but that is untested.
- **Other mods:** any gameplay-affecting mod must also be installed identically on all peers.
  Lockstep applies to the whole process.

Full detail in [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md).

## Install

1. Download `MultiplayerStability-0.8.32.zip` from
   [GitHub Releases](https://github.com/Sternab/MultiplayerStability/releases). Every player installs
   the **same** version.
2. Extract it into
   `%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Modifications\MultiplayerStability`
3. Enable it in the in-game mod manager.
4. Verify in `GameLogFull.txt` after launch: `[MPStability] [Init] Patches applied (45 classes)` with
   no `[Init][ERR]` lines and no `PATTERN NOT FOUND`.

Release hashes and full verification steps: [docs/RELEASE-0.8.32.md](docs/RELEASE-0.8.32.md).

## Documentation

| Document | Contents |
|---|---|
| [TECHNICAL-OVERVIEW.md](docs/TECHNICAL-OVERVIEW.md) | Architecture summary, defect pattern, and design constraints |
| [PATCH-CATALOG.md](docs/PATCH-CATALOG.md) | Canonical inventory of all 23 components: targets, defect, intervention, evidence status |
| [EVIDENCE-MATRIX.md](docs/EVIDENCE-MATRIX.md) | Symptom, mechanism, fix, and validation for each tracked issue |
| [KNOWN-LIMITATIONS.md](docs/KNOWN-LIMITATIONS.md) | What is not fixed, not proven, or out of scope |
| [REPRODUCING.md](docs/REPRODUCING.md) | Boot health check, per-component verification, two-sided capture protocol |
| [TESTING.md](docs/TESTING.md) | Current verification and planned automated tests |
| [BUILDING.md](docs/BUILDING.md) | Building from source against the Owlcat modification template |
| [COMPATIBILITY.md](docs/COMPATIBILITY.md) | Tested vs design-only per game build, player count, platform |
| [ROADMAP-0.9.md](docs/ROADMAP-0.9.md) | Planned 0.9 hardening work, starting with session compatibility |
| [RELEASE-0.8.32.md](docs/RELEASE-0.8.32.md) | Build identity, assembly hashes, artifact verification |
| [docs/investigation/](docs/investigation/) | Lockstep architecture notes, hazard analysis, transfer research, and capture excerpts |

Each source file's header carries its component's detailed rationale;
[docs/PATCH-CATALOG.md](docs/PATCH-CATALOG.md) is the index over them.

## Known limitations

Nine of the 23 components are **shipped but not yet field validated**. Their mechanisms are confirmed
from captures or engine source, but no post-fix two-sided capture has proven the fix. The charge-path
cache fix (C22) still needs the charge, attack, and parry scenario to be repeated. Two desync
classes ship instrumented but unfixed (weather combat-exit, Tactician momentum remainder), and one
single-entity fork remains entirely open.

The status vocabulary is strict: *Field validated* means a post-fix two-sided capture showed the
class absent under conditions that previously produced it. Other statuses state the remaining
validation gap. [docs/KNOWN-LIMITATIONS.md](docs/KNOWN-LIMITATIONS.md) is the full inventory.

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

**@Sternab**. Issues and pull requests are welcome. Raw multi-machine capture logs behind the evidence
matrix are retained privately and can be shared in redacted form on request; two- and three-machine
capture sessions can be run to reproduce a catalogued desync class or validate a proposed change.

## License

MIT. See [LICENSE](LICENSE).

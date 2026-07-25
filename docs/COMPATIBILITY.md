# Compatibility

## Game builds

Developed and **field-tested against Warhammer 40,000: Rogue Trader `1.6.1.514` (Steam)** with the
reference assemblies hashed in `RELEASE-0.8.32.md`. Patch installation is best-effort and per
patch class: after a game patch, a missing target logs `[Init][ERR]` / `[ERR] ... not found` /
`PATTERN NOT FOUND` at boot and the affected patch class stays inert while the rest continue. A
component spanning several classes can be left partially active (`KNOWN-LIMITATIONS.md`). **Check
the boot log after every game update**, and run `tools/check-harmony-targets.py` against the
updated assemblies (standard set in the tool header).

## Player counts

| Configuration | Status |
|---|---|
| 2 players (Steam) | **Field tested.** Primary configuration with an extensive session record |
| 3 players (Steam) | **Field tested.** Includes transfers, barriers, and desync captures |
| 4-6 players | **Design only.** Components are player-count agnostic and the engine caps sessions at 6 players. Save transfer is sequential, at approximately 8 seconds per additional peer. No field record |

## Platform

| Configuration | Status |
|---|---|
| Steam, all peers | **Field tested.** Includes the accelerated P2P transfer |
| GOG (any peer) | **Design only.** The Steam transfer self-disables and falls back to vanilla Photon transfer. Other components are expected to work. No field record |

## Version mixing

See the peer-compatibility categories in `PATCH-CATALOG.md`. Until the 0.9 session-latched gate
ships: **identical mod version on every machine.** The mod's own peer check matches mod *identity*,
not version. A mixed-version lobby will not warn you. Mixed installs of simulation-changing fixes
range from ineffective to actively desync-causing.

## Other mods

- Coexists with OwlcatModification and UnityModManager mods (field record includes ToyBox and
  MicroPatches running alongside; exact third-party versions per capture are recorded in the
  evidence archive).
- Any *gameplay-affecting* mod must itself be installed identically on all peers. Lockstep applies
  to the whole process. Client-side-only UI and cosmetic mods may be installed asymmetrically.
- Known interop hazard for content mods: units whose weapon or view assets differ across machines can
  desync through view-dependent projectile paths. Symmetric installs avoid this.
- The vanilla lobby mod list is a boot-time snapshot of `ActiveUMMItemsInfo.txt`; externally-loaded
  UMM mods appear only as a boolean flag. Do not rely on it for parity auditing.

## DLC

In field sessions, peers with 9 and 7 owned DLCs resolved to the shared set of 7. The lobby handles
this natively. This is an observation, not a guarantee for all DLC combinations.

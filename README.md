# Multiplayer Stability

Multiplayer Stability is a co-op mod for **Warhammer 40,000: Rogue Trader**. It
contains targeted fixes for reproduced desynchronization paths, diagnostics for unresolved cases,
and faster save transfer between players.

The current release is **v1.0.0** for game build **1.6.1.514**.

1.0 is the first stable release, following extended community testing and a tester-reported full
campaign without a desync. It retains the runtime code and content from 0.9.8, with no new gameplay
changes. Known limitations remain documented below. Use the same release on every machine and keep
save backups.

## Features

- Prevents confirmed paths where local camera, fog, view, UI, physics, or cache state changes
  synchronized simulation state.
- Isolates character-preview units from gameplay RNG, facts, auras, UUID allocation, and global
  rulebook subscriptions.
- Adds desync reports with state buckets, full RNG fingerprints, Player child hashes, per-entity
  hashes, and recent creation history.
- Uses Steam Networking Messages for bulk save data when exact-build compatibility is confirmed.
  Photon remains the control path and per-peer fallback.
- Adds framing, transfer identity, size checks, SHA-256 validation, receiver acceptance reporting,
  retries, and timeouts to the mod's custom network paths.
- Sequences selected loading barriers that could otherwise leave a peer waiting indefinitely.

The mod does not start a resync automatically and does not suppress Rogue Trader's desync dialog.

## Requirements

- Rogue Trader build **1.6.1.514**.
- The same Multiplayer Stability version on every player.
- The same gameplay-affecting mods on every player.
- Steam is required only for accelerated save transfer.

GOG and sessions with four to six players have not been field tested. There are no required mod
dependencies.

## Installation

### ModFinder

[ModFinder](https://www.nexusmods.com/warhammer40kroguetrader/mods/146) is the recommended installer.
If Multiplayer Stability is not yet in its catalog:

1. Download `MultiplayerStability-1.0.0.zip` from
   [GitHub Releases](https://github.com/Sternab/MultiplayerStability/releases).
2. Drag the unchanged ZIP onto ModFinder's **Drag zips here to install** area.
3. Confirm that Multiplayer Stability is installed and enabled on every machine.
4. Launch the game, enable the mod from **Mods** if required, and restart when prompted.

### Manual

1. Close the game and download the release ZIP.
2. Create:
   `%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Modifications\MultiplayerStability`
3. Extract the ZIP contents into that folder.
4. Confirm this file exists:
   `...\MultiplayerStability\OwlcatModificationManifest.json`
5. Open this file in a text editor:
   `%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\OwlcatModificationManagerSettings.json`
6. Add `"MultiplayerStability"` to the `EnabledModifications` array. Keep any existing mod names
   and other settings; separate names with commas. If this is your only enabled mod, the entry is
   `"EnabledModifications": ["MultiplayerStability"]`.
7. Save the file, launch the game, and confirm the startup line in
   [Multiplayer Check](#multiplayer-check) below. Repeat these steps on every player's machine.

If the settings file does not exist, create it with the following contents. Make sure its filename
ends in `.json`, not `.json.txt`:

```json
{
  "EnabledModifications": ["MultiplayerStability"]
}
```

Extracting the files alone does not enable the mod. Without the settings entry, it may not appear
on the in-game Mods page or load its patches.

Do not extract directly into `Modifications`, and avoid a doubled
`MultiplayerStability\MultiplayerStability` folder.

## Multiplayer Check

At startup, search `GameLogFull.txt` (see [log location](#troubleshooting-and-reports)) for:

`[MPStability] [Init] Patches applied (`

Do not continue if the line reports `FAILED`, or if any Multiplayer Stability line contains
`[Init][ERR]`, `PATTERN NOT FOUND`, or a component `[ERR]`. Patch classes load independently, so one
startup failure can leave a multi-class component only partly active.

Before a save-transfer launch, peers exchange their compiled module IDs. The peer that starts the
save checks those identities and the advertised manifest versions, then sends one reliable
compatibility decision directly to each other peer before the game's `LoadSave` message. Decisions
are keyed by sender so vanilla's non-owner and simultaneous save-start behavior remains available:

- matching manifest versions and compiled module IDs enable simulation fixes and custom
  protocols;
- a missing or different build selects vanilla simulation and transfer behavior for compatible
  clients that support this protocol (0.9.0 and later);
- if a peer advertises Multiplayer Stability but the save-sender decision is missing or invalid, the
  client refuses the load rather than enter play with a different policy.

Look for `[Compat] Compatible` on every peer when testing the fixes. Pre-0.9 builds do not understand
this decision and remain unsupported in mixed-version sessions. The module ID identifies the
compiled assembly but does not prove that every Harmony patch installed correctly, so clean startup
logs are still required.

## Known Limitations

- The weather combat-exit path and Tactician momentum remainder are instrumented, not fixed. The
  invalid Comfort in Conformity heal trigger is repaired separately, with post-fix field validation
  pending.
- Two consecutive Medikit uses preceded forks in the paired v0.9.2 capture. The second included one
  extra in-tick `GlobalUuid` draw. v0.9.5 records the exact UUID caller but does not alter Medikit
  behavior until that caller is identified.
- The achievement-reward, generalized all-unit combat-order, and Proving Ground extra-turn fixes
  need post-fix field validation. Sorting cannot repair peers that disagree about target membership.
- Dash delivery removes view-position timing from target delivery, but local movement completion can
  still select a different tick.
- Sorting range-query results cannot repair different physics candidate membership.
- Hidden AI turns run at 1x in multiplayer because the faster vanilla branch uses local visibility.
- Projectile mechanics use entity geometry instead of local view bones. Starship projectile geometry
  has limited field coverage.
- The augmentation screen omits its client-random bark in multiplayer.
- The nested-answer "new answers" marker is omitted in exact-parity multiplayer because the vanilla
  UI query can execute and persist an uncached party skill check. Synchronized answer commands use
  current offered-answer identity instead of vanilla's local transient drop guards.
- The `There Is Only War...` future-playthrough amulet is not granted while the exact-parity
  multiplayer fixes are active. The vanilla cabin action uses each peer's local platform achievement
  to decide a shared inventory write; solo reward behavior is unchanged.

Implementation status and evidence are listed in [DESIGN_NOTES.md](DESIGN_NOTES.md).

## Troubleshooting and Reports

The full game log is:

```text
%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\GameLogFull.txt
```

To find it, press **Win+R**, paste the folder path below, and press Enter:

`%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader`

Report problems through [GitHub Issues](https://github.com/Sternab/MultiplayerStability/issues).
For desyncs, freezes, or transfer failures:

1. Keep a copy of `GameLogFull.txt` from **every player, including the host**, before launching the
   game again. Collect logs from the same session and label them by player or host/client role.
2. Describe what happened, the area and action involved, and the approximate time. For a desync,
   record the action immediately before the first dialog.
3. Include the game version, installed mod versions, and startup patch result from every machine.
   ZIP the logs if they are large.
4. Keep the full logs private until account identifiers have been removed.

Logs from long sessions without desyncs are useful too: they show whether fixes were exercised
without errors.

After a Rogue Trader update, treat the mod as unverified until its Harmony targets and reflected
members have been reviewed against the new build.

## Uninstalling

The mod adds no save-required content or blueprints. Disable or remove it on every machine before
the next session. Existing saves then use vanilla multiplayer behavior.

## Building

1. Use `Modding/WhRtModificationTemplate.tar` from the installed Rogue Trader build you are
   targeting. Do not assume that a previously extracted project was updated with the game.
2. When reusing an editor project, close Unity and refresh its `Assets/RogueTraderAssemblies`
   binaries from that template. Preserve existing `.meta` files. Do not copy
   `WH40KRT_Data/Managed` wholesale because the template contains editor-specific assembly variants.
3. Open the template in Unity **6000.0.64f1**.
4. Copy `Assets/Modifications/MultiplayerStability` into the template's
   `Assets/Modifications/` folder.
5. Run `Assets > Modification Tools > Build`.
6. Copy `Blueprints/README.txt` from the source tree into the built mod's `Blueprints` folder. The
   Owlcat build excludes the text placeholder, and an empty ZIP directory may be discarded by an
   installer.
7. Verify the packaged manifest version, DLL timestamp, and non-empty `Blueprints` folder before
   distribution. Timestamps alone do not identify a release; compare file hashes when checking an
   existing package.

The published 1.0.0 package reuses the 0.9.8 DLL and content byte-for-byte; only its external manifest
version changed. Its DLL therefore retains the original build timestamp. Future code changes still
require a fresh Unity build.

The template generates `Generated`; it is intentionally not tracked. No Python tooling is required.

## Credits

- Built with Owlcat Games' Rogue Trader modification template.
- Developed from paired multiplayer logs, decompiled code review, and community testing.
- This is an unofficial fan modification. Warhammer 40,000 and related marks belong to Games
  Workshop. Rogue Trader belongs to Owlcat Games.

## License

MIT. See [LICENSE](LICENSE).

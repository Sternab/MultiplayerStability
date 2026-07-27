# Multiplayer Stability

Multiplayer Stability is an experimental co-op mod for **Warhammer 40,000: Rogue Trader**. It
contains targeted fixes for reproduced desynchronization paths, diagnostics for unresolved cases,
and faster save transfer between players.

The current source is **v0.9.2** for game build **1.6.1.514**.

> **Review build:** v0.9.2 adds fixes for a local-achievement reward fork, Proving Ground
> replacement-buff ordering, and duplicate desync recovery notifications. Use the same version on
> every machine and keep save backups.

## Features

- Prevents confirmed paths where local camera, fog, view, UI, physics, or cache state changes
  synchronized simulation state.
- Isolates character-preview units from gameplay RNG, facts, auras, UUID allocation, and global
  rulebook subscriptions.
- Adds desync reports with state buckets, full RNG fingerprints, per-entity hashes, and recent
  creation history.
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

1. Download `MultiplayerStability-0.9.2.zip` from
   [GitHub Releases](https://github.com/Sternab/MultiplayerStability/releases).
2. Drag the unchanged ZIP onto ModFinder's **Drag zips here to install** area.
3. Confirm that Multiplayer Stability is installed and enabled on every machine.
4. Launch the game, enable the mod from **Mods** if required, and restart when prompted.

### Manual

1. Download the release ZIP.
2. Create:
   `%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Modifications\MultiplayerStability`
3. Extract the ZIP contents into that folder.
4. Confirm this file exists:
   `...\MultiplayerStability\OwlcatModificationManifest.json`
5. Enable the mod in game and restart when prompted.

Do not extract directly into `Modifications`, and avoid a doubled
`MultiplayerStability\MultiplayerStability` folder.

## Multiplayer Check

At startup, search `GameLogFull.txt` for:

`[MPStability] [Init] Patches applied (`

Do not continue if the line reports `FAILED`, or if any Multiplayer Stability line contains
`[Init][ERR]`, `PATTERN NOT FOUND`, or a component `[ERR]`. Patch classes load independently, so one
startup failure can leave a multi-class component only partly active.

Before a save-transfer launch, peers exchange their compiled module IDs. The peer that starts the
save checks those identities and the advertised manifest versions, then sends one reliable
compatibility decision directly to each other peer before the game's `LoadSave` message. Decisions
are keyed by sender so vanilla's non-owner and simultaneous save-start behavior remains available:

- matching v0.9.2 manifest versions and compiled module IDs enable simulation fixes and custom
  protocols;
- a missing or different build selects vanilla simulation and transfer behavior for compatible
  0.9 clients;
- if a peer advertises Multiplayer Stability but the save-sender decision is missing or invalid, the
  client refuses the load rather than enter play with a different policy.

Look for `[Compat] Compatible` on every peer when testing the fixes. Pre-0.9 builds do not understand
this decision and remain unsupported in mixed-version sessions. The module ID identifies the
compiled assembly but does not prove that every Harmony patch installed correctly, so clean startup
logs are still required.

## Known Limitations

- The weather combat-exit path and Tactician momentum remainder are instrumented, not fixed.
- The achievement-reward and Proving Ground fixes need post-fix field validation.
- Dash delivery removes view-position timing from target delivery, but local movement completion can
  still select a different tick.
- Sorting range-query results cannot repair different physics candidate membership.
- Hidden AI turns run at 1x in multiplayer because the faster vanilla branch uses local visibility.
- Projectile mechanics use entity geometry instead of local view bones. Starship projectile geometry
  has limited field coverage.
- The augmentation screen omits its client-random bark in multiplayer.
- The nested-answer "new answers" marker is omitted in exact-parity multiplayer because the vanilla
  UI query can execute and persist an uncached party skill check. Dialogue progression is unchanged.
- The `There Is Only War...` future-playthrough amulet is not granted while the exact-parity
  multiplayer fixes are active. The vanilla cabin action uses each peer's local platform achievement
  to decide a shared inventory write; solo reward behavior is unchanged.

Implementation status and evidence are listed in [DESIGN_NOTES.md](DESIGN_NOTES.md).

## Troubleshooting

`GameLogFull.txt` is stored in:

`%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader`

When reporting a desync:

1. Collect `GameLogFull.txt` from every peer.
2. Record the action immediately before the first desync dialog.
3. Include the installed manifest version and startup patch result from every machine.
4. Keep the full logs private until account identifiers have been removed.

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
   distribution.

The template generates `Generated`; it is intentionally not tracked. No Python tooling is required.

## Credits

- Built with Owlcat Games' Rogue Trader modification template.
- Developed from paired multiplayer logs, decompiled code review, and community testing.
- This is an unofficial fan modification. Warhammer 40,000 and related marks belong to Games
  Workshop. Rogue Trader belongs to Owlcat Games.

## License

MIT. See [LICENSE](LICENSE).

# Multiplayer Stability

A co-op stability mod for **Warhammer 40,000: Rogue Trader**. It fixes several confirmed
desynchronization paths, adds diagnostics for unresolved cases, and accelerates save transfers
between players.

The current release is **v0.8.32** for game build **1.6.1.514**.

> **Experimental release.** Multiplayer Stability is based on paired game logs and source review,
> but some fixes still need post-fix field validation. Every player in a session must use the same
> mod version. The mod never starts a resync automatically.

## Features

- Fixes confirmed cases where local camera, fog, view, physics, UI, or cache state changes hashed
  simulation state.
- Isolates character-preview units from gameplay RNG, facts, auras, and global rulebook handlers.
- Prevents several dialogue, weather, projectile, animation, charge, and trap paths from diverging
  between peers.
- Adds desync reports with state buckets, RNG fingerprints, entity hashes, and recent creation
  history.
- Transfers saves through Steam Networking Messages when available, with Photon retained as the
  control channel and fallback. Test sessions measured an improvement of about 8x.
- Sequences selected loading barriers that could otherwise leave a client waiting at 100 percent.

## Requirements

- Warhammer 40,000: Rogue Trader build **1.6.1.514**.
- The **same Multiplayer Stability version on every player**.
- The same gameplay-affecting mods on every player.
- Steam is required only for accelerated save transfer. Other fixes are expected to work on GOG,
  but GOG has not been field tested.

There are no required mod dependencies.

## Installation

### Recommended: ModFinder

[ModFinder](https://www.nexusmods.com/warhammer40kroguetrader/mods/146) can install and manage
Owlcat template mods. Multiplayer Stability is not yet in its built-in catalog, so install the
release archive directly:

1. Install and run ModFinder.
2. Download `MultiplayerStability-0.8.32.zip` from
   [GitHub Releases](https://github.com/Sternab/MultiplayerStability/releases).
3. Drag the unchanged ZIP onto ModFinder's **Drag zips here to install** area.
4. Confirm that **Multiplayer Stability** is installed and enabled on every player's machine.
5. Launch the game, open **Mods** from the title screen, enable the mod if required, and restart
   when prompted.

### Manual

1. Download `MultiplayerStability-0.8.32.zip` from the Releases page.
2. Create this folder:
   `%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Modifications\MultiplayerStability`
3. Extract the contents of the ZIP into that folder. The final path must be:
   `...\Modifications\MultiplayerStability\OwlcatModificationManifest.json`
4. Launch the game, open **Mods** from the title screen, enable **Multiplayer Stability**, and
   restart when prompted.

Do not extract the files directly into `Modifications`, and avoid an extra nested
`MultiplayerStability\MultiplayerStability` folder.

## Multiplayer Use

Before starting or joining a session:

1. Check that every player has the same game build and mod list.
2. Check that every player has the same Multiplayer Stability version.
3. After launch, search `GameLogFull.txt` for:
   `[MPStability] [Init] Patches applied (45 classes)`
4. Do not continue if any player has `[MPStability] [Init][ERR]` or `PATTERN NOT FOUND` lines.

The current release does not enforce version parity itself. Mixed installations can create new
desyncs because several fixes change simulation behavior.

## Known Issues

- Two desync classes are instrumented but not fixed: weather selection at combat exit and the
  Tactician momentum remainder.
- The charge-path and trap containment fixes still need dedicated post-fix reproductions.
- Several newer fixes have source-confirmed mechanisms but limited field coverage.
- Hidden AI turns run at 1x in co-op because the vanilla fast-forward condition depends on local
  visibility state.
- The augmentation screen does not play its random bark in co-op.
- Projectile mechanics use the target entity's base point instead of local view bones.
- Four to six players and GOG multiplayer remain untested.

See [DESIGN_NOTES.md](DESIGN_NOTES.md) for the component list, validation status, tradeoffs, and
planned 0.9 hardening work.

## Uninstalling and Saves

The mod adds no save-required content or blueprints. To remove it, disable or uninstall it on every
player's machine before the next session. Existing saves then return to vanilla multiplayer
behavior.

Keep a save backup before changing any multiplayer mod set.

## Troubleshooting

`GameLogFull.txt` is in:

`%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader`

Search for `MPStability`. When reporting a desync, include `GameLogFull.txt` from every player and
note the action immediately before the first desync notification.

Common checks:

- Confirm the installed manifest reports the expected version.
- Confirm every peer uses the same build.
- Confirm the install path is not nested twice.
- Check startup for `[Init][ERR]` and `PATTERN NOT FOUND`.
- After a game update, treat the mod as unverified until its Harmony targets have been reviewed.

## Building from Source

1. Get Owlcat Games' official `WhRtModificationTemplate`.
2. Open the template in Unity **6000.0.64f1**.
3. Copy
   `Assets/Modifications/MultiplayerStability`
   from this repository into the template's `Assets/Modifications/` folder.
4. In Unity, run `Assets > Modification Tools > Build`.
5. Verify the generated manifest version and DLL timestamp before installing or distributing the
   result.

The template generates the `Generated` folder during the build. It is intentionally not tracked.
No Python tooling is required to build the mod.

Implementation notes are in [DESIGN_NOTES.md](DESIGN_NOTES.md). Each C# file also documents its
Harmony targets, reason for patching, multiplayer gate, and failure behavior.

## Credits

- Built with Owlcat Games' official Rogue Trader modification template.
- Developed from paired multiplayer logs, decompiled game-code review, and testing by the Rogue
  Trader modding community.
- This is an unofficial fan modification. Warhammer 40,000 and related marks belong to Games
  Workshop. Rogue Trader belongs to Owlcat Games.

## License

MIT. See [LICENSE](LICENSE).

# Release v0.8.32

Build identity and verification data for the `v0.8.32` release. The installable artifact is attached
to the [GitHub release](https://github.com/Sternab/MultiplayerStability/releases/tag/v0.8.32).
This document records the source and artifact identity and explains how to verify it.

Per-component validation status is in [PATCH-CATALOG.md](PATCH-CATALOG.md); several shipped fixes are
still awaiting post-fix field validation ([KNOWN-LIMITATIONS.md](KNOWN-LIMITATIONS.md)).

## Source identity

| Item | Value |
|---|---|
| Release tag | `v0.8.32` = commit `3ab7a4a`, the source used to build the released DLL |
| Manifest `UniqueName` / `Version` | `MultiplayerStability` / `0.8.32` |
| Source files | 25 `.cs` (23 numbered components) plus Unity `.meta`, manifest, localization, docs |

Commits on `main` after the `v0.8.32` tag change **no executable C# statement**. The delta is
documentation, source comments, the tools under `tools/`, a `Content/README.txt` placeholder,
`.gitignore`, and the `LICENSE` attribution line. Reproduce the C# half with:

```bash
python tools/verify-comments-only.py v0.8.32..main
```

## Build environment

| Item | Value |
|---|---|
| Game version (all field sessions) | Warhammer 40,000: Rogue Trader `1.6.1.514` (Steam) |
| Unity editor | `6000.0.64f1` |

These are the assemblies the mod compiles against and the target checker reads. The modding
template's reference assemblies are not byte-identical to the game installation assemblies. All 57
documented target rows (55 patch targets plus 2 reflection dependencies the patches call into)
resolve against either set. This was verified against both sets and the original `Code.dll` shipped
with the template.

| Assembly | Source used here | Bytes | SHA-256 |
|---|---|---|---|
| `Code.dll` | template `Assets/RogueTraderAssemblies/` (see [BUILDING.md](BUILDING.md); this copy is locally re-serialized) | 14,228,480 | `33002BB397EB044C3C2425F1342C5C21671023EA1989A7594B573E3879A33420` |
| `RogueTrader.GameCore.dll` | template `Assets/RogueTraderAssemblies/` | 400,384 | `BA929D3AE15F013B42A18B043178C9217392CC60E409323353C37788C8F1D00B` |
| `StatefulRandom.dll` | template (byte-identical to the game install's copy) | 14,848 | `49DB59F9CF63E89F03FC191B6D8E127C4DA0901174270066CE632261774007A4` |
| `Owlcat.Runtime.Visual.dll` | **game install** `WH40KRT_Data/Managed/` (not present in the template) | 1,027,584 | `163CADDA6B8F0BF2D79D76270A804525584E0E2760F228C87C7F714E689582BD` |
| `0Harmony.dll` | game-supplied Harmony the mod patches through; the template's copy is byte-identical | 909,824 | `9611251F080E4855CD9D5FA54B5E56034DF25B70B374C49BA09550B08A1BF875` |

The mod ships no Harmony of its own.

## Release artifact

| Item | Value |
|---|---|
| File | `MultiplayerStability-0.8.32.zip` (attached to the GitHub release) |
| Size / build timestamp | 48,500 bytes; DLL and package entries built 2026-07-24 06:21:14 (local) |
| SHA-256 | `95CCBD1C1B02C0BEB6C807C2F1F1FE36C1EF86A409B30DF6E1841DA9BBA02B89` |
| `Assemblies/MultiplayerStability.dll` | 111,104 bytes, SHA-256 `B954483BB63C53C2F25910F32CDB69340CDE2A7BDFA5B205AB5880722B596B05` |

### Contents

```
OwlcatModificationManifest.json      (Version 0.8.32)
OwlcatModificationSettings.json
Assemblies/MultiplayerStability.dll  (111,104 B)
Blueprints/                          (required by the loader even when empty)
Bundles/MultiplayerStability_BlueprintDirectReferences
Content/
Localization/enGB.json
```

The copy installed on the field-test machine is a separate Unity build of the same tagged source: the
manifest version matches, but its DLL hash differs because Unity builds are not bit-reproducible
(MVID and timestamp metadata). A method-body comparison verified that the two assemblies are identical
apart from build metadata. The
released DLL above is the authoritative binary.

## Verifying a download

```powershell
Get-FileHash MultiplayerStability-0.8.32.zip -Algorithm SHA256
#   expect 95CCBD1C1B02C0BEB6C807C2F1F1FE36C1EF86A409B30DF6E1841DA9BBA02B89
Expand-Archive MultiplayerStability-0.8.32.zip -DestinationPath .\pkg
Get-FileHash .\pkg\Assemblies\MultiplayerStability.dll -Algorithm SHA256
#   expect B954483BB63C53C2F25910F32CDB69340CDE2A7BDFA5B205AB5880722B596B05
```

`tools/verify-package.ps1` automates this. Run from a checkout of this repository; pass
`-AllowDllOlderThanSource` when checking the released zip against a working tree that is ahead of the
tag, since the post-tag documentation commits legitimately postdate the frozen build.

`tools/check-harmony-targets.py` verifies that every documented Harmony target still resolves across
the assembly set above (metadata-only, no game launch required); with `--reconcile Scripts` it also
fails if the source contains a patch site the documented table does not cover.

## Attribution note

The `LICENSE` attribution line was shortened to the author's handle after this tag was cut, so the
tree at `v0.8.32` carries the older wording. The release artifact and the licence terms are unchanged
by that edit.

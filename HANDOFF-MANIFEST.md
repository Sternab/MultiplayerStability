# Handoff Manifest — MultiplayerStability v0.8.32

Exact identity of the **v0.8.32 review artifact** (built from the tagged source; this exact build
has not completed post-fix field validation) and of the source delivered for review. Per-component validation status lives in `PATCH-CATALOG.md`; several shipped
fixes are still awaiting post-fix validation (`KNOWN-LIMITATIONS.md`).

## Source identity

| Item | Value |
|---|---|
| Mod source: build tag | `3ab7a4a` = tag `v0.8.32` — the exact source the packaged DLL was built from |
| Mod source: review head | resolved at packaging time — see `SNAPSHOT-COMMIT.txt` in this folder and the outer `README-HANDOFF.md` (a committed file cannot contain its own commit hash) |
| Docs repository: build tag | `bb678d1` = tag `docs-v0.8.32` |
| Docs repository: review head | `3c8ba3d` (ahead of its build tag by documentation only) |
| Manifest `UniqueName` / `Version` | `MultiplayerStability` / `0.8.32` |
| Source files | 25 `.cs` (23 numbered components) + Unity `.meta`, manifest, localization, docs |

**Delta from the build tag to the review head:** no commit after `v0.8.32` changes any executable
C# statement. The delta is documentation, source comments, the three tools under `tools/`, a
`Content/README.txt` placeholder, `.gitignore`, and the `LICENSE` attribution line. The C# half is
reproducible in a checkout of the mod repository with `python tools/verify-comments-only.py`; for a
recipient holding only this snapshot, the tag-to-head diff is available on request.

## Build environment

| Item | Value |
|---|---|
| Game version (all field sessions) | Warhammer 40,000: Rogue Trader `1.6.1.514` (Steam) |
| Unity editor | `6000.0.64f1` |

The assemblies the mod compiles against and the target checker reads. **Provenance matters: the
template's reference assemblies are not byte-identical to the game install's.** All 57 documented target rows (55 patch
targets plus 2 reflection dependencies the patches call into) resolve against either set (verified on both, plus the template-shipped original `Code.dll`).

| Assembly | Source used here | Bytes | SHA-256 |
|---|---|---|---|
| `Code.dll` | template `Assets/RogueTraderAssemblies/` (see `BUILDING.md` — this copy is locally re-serialized) | 14,228,480 | `33002BB397EB044C3C2425F1342C5C21671023EA1989A7594B573E3879A33420` |
| `RogueTrader.GameCore.dll` | template `Assets/RogueTraderAssemblies/` | 400,384 | `BA929D3AE15F013B42A18B043178C9217392CC60E409323353C37788C8F1D00B` |
| `StatefulRandom.dll` | template (byte-identical to the game install's copy) | 14,848 | `49DB59F9CF63E89F03FC191B6D8E127C4DA0901174270066CE632261774007A4` |
| `Owlcat.Runtime.Visual.dll` | **game install** `WH40KRT_Data/Managed/` (not present in the template) | 1,027,584 | `163CADDA6B8F0BF2D79D76270A804525584E0E2760F228C87C7F714E689582BD` |
| `0Harmony.dll` | game-supplied Harmony the mod patches through; the template's copy is byte-identical | 909,824 | `9611251F080E4855CD9D5FA54B5E56034DF25B70B374C49BA09550B08A1BF875` |

The mod package ships no Harmony of its own.

## Packaged artifact

| Item | Value |
|---|---|
| Package | `MultiplayerStability-0.8.32.zip` (built as `MultiplayerStability.zip`; renamed in the handoff package) |
| Size / build timestamp | 48,500 bytes; DLL and package entries built 2026-07-24 06:21:14 (local) |
| Package SHA-256 | `95CCBD1C1B02C0BEB6C807C2F1F1FE36C1EF86A409B30DF6E1841DA9BBA02B89` |
| `Assemblies/MultiplayerStability.dll` | 111,104 bytes, SHA-256 `B954483BB63C53C2F25910F32CDB69340CDE2A7BDFA5B205AB5880722B596B05` |

### Package contents

```
OwlcatModificationManifest.json      (Version 0.8.32)
OwlcatModificationSettings.json
Assemblies/MultiplayerStability.dll  (111,104 B)
Blueprints/                          (required by the loader even when empty)
Bundles/MultiplayerStability_BlueprintDirectReferences
Content/
Localization/enGB.json
```

The installed copy on the field-test machine is a separate Unity build of the same tagged source:
manifest version matches, but its DLL hash differs from the packaged DLL (Unity builds are not
bit-reproducible — MVID/timestamp metadata); all method bodies were verified identical by external
review. The packaged DLL above is the authoritative binary.

## Source file inventory

The authoritative per-file listing is the delivered snapshot itself. To hash every file in it:

```powershell
Get-ChildItem -Recurse -File | Get-FileHash -Algorithm SHA256
```

`PATCH-CATALOG.md` maps every source file to its component, targets, and status.

## Verification commands

```powershell
# from the handoff package root:
Get-FileHash MultiplayerStability-0.8.32.zip -Algorithm SHA256
#   expect 95CCBD1C1B02C0BEB6C807C2F1F1FE36C1EF86A409B30DF6E1841DA9BBA02B89
Expand-Archive MultiplayerStability-0.8.32.zip -DestinationPath .\pkg
Get-FileHash .\pkg\Assemblies\MultiplayerStability.dll -Algorithm SHA256
#   expect B954483BB63C53C2F25910F32CDB69340CDE2A7BDFA5B205AB5880722B596B05
```

`tools/verify-package.ps1` (in this repository) automates package verification. When run from the
review snapshot, pass `-AllowDllOlderThanSource`: the snapshot's comment-only edits postdate the
frozen build, and the freshness check reports that honestly otherwise.
`tools/check-harmony-targets.py` verifies that every documented Harmony target still resolves
across the assembly set above (metadata-only, no game launch required), and with `--reconcile
Scripts` also fails if the source contains a patch site the documented table does not cover.

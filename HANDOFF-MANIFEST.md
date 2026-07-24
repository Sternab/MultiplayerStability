# Handoff Manifest — MultiplayerStability v0.8.32

Immutable identity of the field-tested baseline delivered for Owlcat engineering review.

## Source identity

| Item | Value |
|---|---|
| Mod source repository commit | `3ab7a4a` (tag `v0.8.32`) |
| Investigation docs repository commit | `bb678d1` (tag `docs-v0.8.32`) |
| Manifest `UniqueName` / `Version` | `MultiplayerStability` / `0.8.32` |
| Source files | 25 `.cs` (23 numbered components) + Unity `.meta`, manifest, localization, docs |

## Build environment

| Item | Value |
|---|---|
| Game version (field-tested) | Warhammer 40,000: Rogue Trader `1.6.1.514` (Steam) |
| Unity editor | `6000.0.64f1` |
| `Code.dll` (reference assembly) SHA-256 | `33002BB397EB044C3C2425F1342C5C21671023EA1989A7594B573E3879A33420` |
| `0Harmony.dll` (bundled Harmony) SHA-256 | `9611251F080E4855CD9D5FA54B5E56034DF25B70B374C49BA09550B08A1BF875` |

## Packaged artifact

| Item | Value |
|---|---|
| Package | `MultiplayerStability.zip` |
| Size / timestamp | 48,500 bytes / 2026-07-24 06:21:14 (local) |
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

The installed copy on the field-test machine matches the package (same manifest version, same DLL bytes).

## Source file inventory

The authoritative per-file listing with content hashes is produced by:

```bash
git ls-tree -r v0.8.32
```

in the mod repository (Git blob SHA-1 per file, stable for the tag). `PATCH-CATALOG.md` maps every
source file to its component, targets, and status.

## Verification commands

```powershell
Get-FileHash MultiplayerStability.zip -Algorithm SHA256      # expect 95CCBD1C...
Get-FileHash Assemblies\MultiplayerStability.dll -Algorithm SHA256  # expect B954483B...
```

`tools/verify-package.ps1` (in this repository) automates package verification;
`tools/check-harmony-targets.py` verifies every documented Harmony target still resolves in a given
`Code.dll` (metadata-only, no game launch required).

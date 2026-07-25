# Building from source — reproducible procedure

Verified against the v0.8.32 baseline (`HANDOFF-MANIFEST.md` holds all hashes referenced here).

## Prerequisites

| Requirement | Exact value |
|---|---|
| Unity editor | **6000.0.64f1** |
| Project | Owlcat's `WhRtModificationTemplate` (the official Rogue Trader modification template), set up per Owlcat's modding documentation |
| Reference assemblies | in the template's `Assets/RogueTraderAssemblies/`. **Do not expect the `Code.dll` hash in `HANDOFF-MANIFEST.md` to match yours:** the copy compiled against here is a locally re-serialized `Code.dll` (`33002BB3…`, 14,228,480 B) that displaced the template-shipped one (`8505AA6E…`, 14,228,992 B, kept beside it); a 1.6.1.514 game install carries a third variant again (`3F94862B…`). All 57 documented target rows (55 patch targets plus 2 reflection dependencies) resolve against all three — verified — so build against whichever your template ships and confirm with `tools/check-harmony-targets.py`. `0Harmony.dll` SHA-256 `9611251F080E4855CD9D5FA54B5E56034DF25B70B374C49BA09550B08A1BF875` |
| Game (for runtime verification) | Warhammer 40,000: Rogue Trader `1.6.1.514` (Steam) |

Any template location works; no path in this repository assumes a particular drive or folder.

## Repository placement

Clone/check out this repository at:

```
<template project root>/Assets/Modifications/MultiplayerStability
```

(The template's own `.gitignore` excludes `Assets/Modifications/*`, so this nested repository does not
conflict with the template's version control.)

## Compile ≠ package ≠ install — three distinct steps

This distinction has caused real field incidents; treat each step as separate and verify it.

1. **Compile:** opening the project makes Unity compile the scripts into the editor's script
   assemblies. This alone produces **no** distributable.
2. **Package:** run the template's Owlcat modification build (Unity menu:
   **Modifications → Build** — select `MultiplayerStability`). Output:
   `<template project root>/Build/MultiplayerStability.zip` containing
   `OwlcatModificationManifest.json`, `OwlcatModificationSettings.json`,
   `Assemblies/MultiplayerStability.dll`, `Blueprints/`, `Bundles/…BlueprintDirectReferences`,
   `Content/`, `Localization/enGB.json`.
3. **Install:** extract the package to
   `%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Modifications\MultiplayerStability`
   and enable the mod in the in-game mod manager.

## `Blueprints/` directory

The Owlcat loader logs a startup exception if the installed mod lacks a `Blueprints` directory (even
empty). The source tree tracks `Blueprints/README.txt` and the v0.8.32 package contains the directory
entry; if a future packaging pass drops empty directories, recreate `Blueprints/` in the installed
folder.

## Verify every build (the stale-artifact discipline)

```powershell
# package identity
Get-FileHash Build\MultiplayerStability.zip -Algorithm SHA256
# inside the zip / installed folder:
#   manifest "Version" must equal MultiplayerStability.asset's Version
#   Assemblies\MultiplayerStability.dll timestamp must postdate your last source edit
```

`tools/verify-package.ps1` automates this (see `TESTING.md`). A stale DLL has cost entire field
sessions; never distribute on the strength of a Unity compile alone.

## Runtime verification (first launch after any build or game update)

Expected in `GameLogFull.txt`:

- `[MPStability] [Init] Patches applied (45 classes)` with **no** `[Init][ERR]` lines
  (45 = the count of `[HarmonyPatch]`-annotated classes in v0.8.32 source);
- transpiler swap counts: `[FogGate]` six lines, `[TimeScaleFix]` two, `[IdleRng]` four
  (counts 5/1/1/4), `[ProjectileFix]` one;
- no `PATTERN NOT FOUND` lines.

Runtime arming lines appear on first qualifying events (full list in `REPRODUCING.md`).

## Clean rebuild after stale artifacts

1. Close Unity. Delete `Build/MultiplayerStability.zip`.
2. Reopen the project (recompile), run the modification build again.
3. Re-verify: zip timestamp, manifest version, DLL timestamp/hash, then reinstall.

## Release discipline

- Bump `Version` in `MultiplayerStability.asset` for every behavior change.
- Every simulation-changing release ships to **all** peers together (`COMPATIBILITY.md`).
- Evidence bar: a prevention fix is *field validated* only after a post-fix two-sided capture on the
  scenario it fixes (`PATCH-CATALOG.md` statuses; "compiles and loads" is never a validation claim).
- After a game update: run `tools/check-harmony-targets.py` against the updated assemblies
  (standard set in the tool header), then do the
  runtime verification above; re-diff `DeterministicSleep`'s replicated vanilla verdict against
  `SleepingUnitsController.ShouldBeSleeping`.

# Building from source

## Prerequisites
- Unity (the version pinned by Owlcat's `WhRtModificationTemplate`) with the template project set up per
  Owlcat's modding documentation.
- This repository checked out at `Assets/Modifications/MultiplayerStability` inside the template project.

## Build
1. Open the template project in Unity.
2. Ensure `MultiplayerStability.asset` (the OwlcatModification manifest) shows the intended `Version`.
3. Build the modification via the template's build pipeline (Owlcat modification build menu). The build
   output installs to
   `%USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Modifications\MultiplayerStability`.
4. **Verify the deploy is fresh** — a stale DLL has cost entire test sessions:
   - check the DLL's timestamp under `Modifications\MultiplayerStability\Assemblies\`;
   - launch and confirm `[MPStability] [Init] Patches applied (N classes)` with no `FAILED`, plus the
     per-component arming lines (`[FogGate] ... site(s)`, `[TimeScaleFix]`, `[IdleRng]` ×4, etc.).

## Known packaging nuance
The deployed package must contain a `Blueprints` directory (even empty) — the OwlcatModification loader
logs a startup exception when it is missing. The source tree tracks `Blueprints/README.txt` for this reason;
if a build/deploy drops empty directories, recreate `Modifications\MultiplayerStability\Blueprints` in the
deployed folder.

## Release discipline
- Bump `Version` in `MultiplayerStability.asset` for every behavior change (review history stays unambiguous).
- Every simulation-changing release must ship to **all** peers together (see `COMPATIBILITY.md`).
- Field evidence bar: a prevention fix is "validated" only after a clean two-sided session capture on the
  scenario it fixes — static "it compiles and loads" is never claimed as fixed.

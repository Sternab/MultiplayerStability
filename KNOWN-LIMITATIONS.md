# Known Limitations, Open Investigations, and Compromises — v0.8.32

Honest inventory. Nothing here is hidden in file headers.

## Open desync investigations (instrumented, not fixed)

1. **Weather combat-exit class (E22).** One peer drew hashed `Weather` once more at combat exit; the
   gating inputs include the *visual* `IsProfileOverriden` flag. `WeatherCombatExitDiag` logs the full
   predicate set; the differing input is not yet identified. Do **not** wrap this path in
   `DisableStatefulRandomContext` (the draws write hashed player fields).
2. **Tactician remainder class (E18).** `MomentumThisCombat` is omitted from its component hash, so a
   remainder split is invisible until a one-sided 100-crossing. Origin of the split unproven —
   plausibly downstream of the now-fixed charge-path defect, plausibly independent. `TacticianDiag`
   armed. Related audit not yet done: `MomentumReachedTrigger`, `HunterDodge`, `ChangeVeilDamage`
   share the base-only-hash omission.
3. **Area-effect candidate census (E19).** Aura membership candidates come from local Unity
   `OnTriggerEnter2D` callbacks; different candidate *sets* per machine are beyond the shipped
   predicate repair (C08). A tightly scoped diagnostic (trigger entry + final predicate + membership
   for the convicted aura) is designed but not shipped. Any deterministic-census redesign waits for
   that evidence.
4. **Eogann single-entity fork (E21).** One companion's `sceneEntities` hash diverged with every
   recorded stream, creation, and diagnostic matching. Needs per-entity hash *decomposition* (core
   fields / parts / facts / command state per mismatching entity) — designed, not shipped.
5. **`UnitFollowUnitController.ShouldAct`** schedules simulation commands off
   `View.MovementAgent.WantsToMove` (client-local) — can diverge with no exception. Audit item; not
   instrumented.
6. **Dodge / special-attack animation variant picks** share the hashed-stream property the idle fix
   rerouted, but fire inside synced combat execution; left untouched pending their own audit.

## Fixes shipped but not yet field validated (see PATCH-CATALOG for details)

- Charge-path partial-cache disable (C22) — **the charge → attack → parry same-tile scenario must
  pass on ≥0.8.30 before any validation claim.**
- Trap/pause containment (C20), augmentation bark containment (C21), preview `Copy` scope (C10 patch
  3), four newer FogGate sites (C08), dash delivery (C09), local time-scale (C13), physics-order sort
  (C14), projectile geometry (C15).

## Structural limitations

- **Peer parity is manual.** All simulation-changing fixes require the identical build on every
  machine; nothing enforces this until the 0.9 session latch. The vanilla lobby mod list is a
  boot-time file snapshot and cannot see externally-loaded UMM mods.
- **Bucket attribution is inferred**, not proven per-tick (±40-tick `senderTick` spread observed
  between peers for the same mismatch). It steers logs and the flap-dialog policy; 0.9 item 5 adds
  tick identity before it is trusted further.
- **LeakDetector blind spots:** off-thread draws (pathfinding), and any "view flag read by mechanics"
  leak that never passes through `Rand.Get`. Warning budget is per stream (startup noise can mute
  later sites) until 0.9 item 5.
- **Reflection-based seams drift with game updates.** Every patch fails open with loud `[ERR]` /
  `PATTERN NOT FOUND` lines; after any game patch the boot log must be checked (see `TESTING.md`).
  `tools/check-harmony-targets.py` verifies target resolution against a given `Code.dll` offline.
- **The census replaces a vanilla pass.** `DeterministicSleep` replicates vanilla's ambient verdict
  verbatim; that replica must be re-diffed against `SleepingUnitsController.ShouldBeSleeping` after
  every game update.
- **Static `FieldRefAccess` initializers** outside guarded `Prepare()` exist in older components
  (e.g. `ProjectilePositionFix`); a rename would break the patched method rather than declining. The
  trap fix established the safe pattern; audit queued in 0.9 item 5.

## Deliberate compromises (working as intended)

- Hidden AI turns run at 1× in co-op (the fog-gated 16× fast-forward is client-local; a deterministic
  camera-frustum substitute was falsified — local renderer bounds).
- Ambient scene-loops beyond 25 m of the party pause until approached (lockstep-deterministic
  cutscene hold; radius tuned from field FPS data).
- Projectiles aim at the target's base point in MP (identical to vanilla's SnapMap-less rendering).
- The augmentation screen plays no bark in co-op (its pick was client-random and unsynced).
- A "random" dialogue cue previewed in the UI may not match the cue chosen at advancement (preview
  draws are diverted; the advancement draw is synced and authoritative).
- Solo is vanilla except two narrow always-on items (weather VFX draws divert to a non-hashed
  fallback; the projectile aim-bone pick is fixed-choice) — documented as "solo-safe, not
  byte-identical."

## Untested configurations

- **4–6 players:** supported by design (all components player-count-agnostic; engine cap 6); field
  record covers 2 players extensively and 3 players in real sessions. Save transfer is sequential
  per peer (~8 s each) — untested beyond 3.
- **GOG (any peer):** by design the Steam transfer disables and everything else works; **not field
  tested**.
- **Mixed game versions / non-`1.6.1.514` builds:** unverified; target resolution must be re-checked.
- **Dedicated post-fix reproductions** for everything in the "not yet field validated" list above.

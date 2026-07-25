# Known Limitations and Open Investigations

This document describes the current limitations of v0.8.32.

## Open desync investigations (instrumented, not fixed)

1. **Weather combat-exit class (E22).** One peer drew hashed `Weather` once more at combat exit; the
   gating inputs include the *visual* `IsProfileOverriden` flag. `WeatherCombatExitDiag` logs the full
   predicate set; the differing input is not yet identified. Do **not** wrap this path in
   `DisableStatefulRandomContext` (the draws write hashed player fields).
2. **Tactician remainder class (E18).** `MomentumThisCombat` is omitted from its component hash, so a
   remainder split is invisible until a one-sided 100-crossing. The origin of the split is unproven
   and may be downstream of the now-fixed charge-path defect or independent. `TacticianDiag` is
   enabled. Related audit not yet done: `MomentumReachedTrigger`, `HunterDodge`, `ChangeVeilDamage`
   share the base-only-hash omission.
3. **Area-effect candidate census (E19).** Aura membership candidates come from local Unity
   `OnTriggerEnter2D` callbacks; different candidate *sets* per machine are beyond the shipped
   predicate repair (C08). A tightly scoped diagnostic (trigger entry + final predicate + membership
   for the affected aura) is designed but not shipped. A deterministic-census redesign requires
   candidate-set evidence first.
4. **Eogann single-entity fork (E21).** One companion's `sceneEntities` hash diverged with every
   recorded stream, creation, and diagnostic matching. Needs per-entity hash *decomposition* (core
   fields, parts, facts, and command state per mismatching entity). This is designed but not shipped.
5. **`UnitFollowUnitController.ShouldAct`** schedules simulation commands off
   `View.MovementAgent.WantsToMove` (client-local). This can diverge without an exception. Audit item; not
   instrumented.
6. **Dodge / special-attack animation variant picks** share the hashed-stream property the idle fix
   rerouted, but fire inside synced combat execution; left untouched pending their own audit.

## Fixes shipped but not yet field validated (see PATCH-CATALOG for details)

- Charge-path partial-cache disable (C22). **The charge, attack, and parry same-tile scenario must
  pass on version 0.8.30 or later before any validation claim.**
- Trap/pause containment (C20), augmentation bark containment (C21), preview `Copy` scope (C10 patch
  3), four newer FogGate sites (C08), dash delivery (C09), local time-scale (C13), physics-order sort
  (C14), projectile geometry (C15).

## Structural limitations

- **Peer parity is manual.** All simulation-changing fixes require the identical build on every
  machine; nothing enforces this until the 0.9 session latch. The vanilla lobby mod list is a
  boot-time file snapshot and cannot see externally-loaded UMM mods.
- **Bucket attribution is inferred**, not proven per-tick (+/-40-tick `senderTick` spread observed
  between peers for the same mismatch). It steers logs and the flap-dialog policy; 0.9 item 5 adds
  tick identity before it is trusted further.
- **LeakDetector blind spots:** off-thread draws (pathfinding), and any "view flag read by mechanics"
  leak that never passes through `Rand.Get`. Warning budget is per stream (startup noise can mute
  later sites) until 0.9 item 5.
- **Reflection-based seams can drift after game updates. Fail-open behavior is best effort and local
  to each patch site.**
  Isolation is per patch class: a failing class logs `[Init][ERR]` and stays inert while others
  continue, so a component spanning several classes, or a class whose targets partially resolve,
  can be left **partially active** (the boot message's "component inert" wording refers to the failed
  patch class, not the whole component). Runtime guards fall back to the vanilla path at their own
  site with explicit `[ERR]` / `PATTERN NOT FOUND` lines. After any game patch the boot log must be
  checked (see `TESTING.md`); `tools/check-harmony-targets.py` verifies target resolution offline.
- **The census replaces a vanilla pass.** `DeterministicSleep` replicates vanilla's ambient verdict
  verbatim; that replica must be re-diffed against `SleepingUnitsController.ShouldBeSleeping` after
  every game update.
- **Static `FieldRefAccess` initializers** outside guarded `Prepare()` exist in older components
  (for example, `ProjectilePositionFix`); a rename would break the patched method rather than declining. The
  trap fix established the safe pattern; audit queued in 0.9 item 5.

## Behavioral tradeoffs

- Hidden AI turns run at 1x in co-op (the fog-gated 16x fast-forward is client-local; a deterministic
  camera-frustum substitute was rejected because it depends on local renderer bounds).
- Ambient scene-loops beyond 25 m of the party pause until approached (lockstep-deterministic
  cutscene hold; radius tuned from field FPS data).
- Projectiles aim at the target's base point in MP (identical to vanilla's SnapMap-less rendering).
- The augmentation screen plays no bark in co-op (its pick was client-random and unsynced).
- A "random" dialogue cue previewed in the UI may not match the cue chosen at advancement (preview
  draws are diverted; the advancement draw is synced and authoritative).
- Solo is vanilla except for two narrow always-on items (weather VFX draws divert to a non-hashed
  fallback; the projectile aim-bone pick is fixed-choice). These are documented as "solo-safe, not
  byte-identical."

## Untested configurations

- **4-6 players:** supported by design (all components player-count agnostic; engine cap 6); field
  record covers 2 players extensively and 3 players in real sessions. Save transfer is sequential
  per peer (approximately 8 seconds each). This is untested beyond 3 players.
- **GOG (any peer):** by design the Steam transfer disables and other components are expected to work; **not field
  tested**.
- **Mixed game versions / non-`1.6.1.514` builds:** unverified; target resolution must be re-checked.
- **Dedicated post-fix reproductions** for everything in the "not yet field validated" list above.

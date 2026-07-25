# Reproducing and Verifying — v0.8.32

Procedures for reproducing the addressed defect classes and verifying the mod's behavior. All
multiplayer procedures need two machines (or the engine's own `net_allow_one` cheat for single-machine
smoke tests) on the **identical** mod build.

## Universal capture protocol

1. Play until an event of interest (or a desync) occurs; note the context immediately (combat state,
   area, which screen was open, who hosted).
2. **Before any relaunch on either machine**, copy `GameLogFull.txt` from *both* machines (each launch
   rotates `GameLogFull.txt` → `GameLogFullPrev.txt` and destroys the previous `Prev`). One-sided
   captures identify suspects at best.
3. Diff the `[MPStability]` instrumentation between the logs: episode markers, bucket attributions,
   RNG-stream fingerprints (`rng streams advanced near tick`), uuid rings
   (`entities/facts created near tick`), per-entity hash dumps (serious episodes only).

## Boot health check (every build, every game update)

Expected in each machine's log after launch:

- `[MPStability] [Init] Patches applied (45 classes)` and **no** `[Init][ERR]` lines.
- Transpiler counts: `[FogGate]` six site lines; `[TimeScaleFix]` ×2; `[IdleRng]` ×4 (counts 5/1/1/4);
  `[ProjectileFix]` swap line.
- No `PATTERN NOT FOUND` lines (each means that one site reverted to vanilla).

Runtime-proof lines appear on first qualifying events, not at boot: `[ChargeFix] Active`,
`[DialogRng]` guard lines, `[TrapFix] Containment active`, `[BarkFix] Active`, `[GhostFix]`,
`[OrderFix] Active`, `[ActionBarFix] Active`, `[DetSleep] census`, `[Transfer] Steam P2P path`.

## Per-class procedures

### Charge / parry same-tile (E17 — the required validation for C22)

1. Both machines on ≥0.8.30. In turn-based combat, **hover a charge path that extends past an enemy**,
   then retarget that enemy and execute the charge; melee-attack; ideally against a parry-capable
   enemy. Repeat several charges.
2. Expected with the fix: `[ChargeDiag] path source=` lines showing only `exact-cache` or `computed`
   in MP (never `partial-cache`), `[ChargeFix] Active` once, no same-tile overlap, no desync.
3. Record whether **both** players see identical positions after each charge. Any `partial-cache`
   line in MP is a fix-not-holding alarm regardless of desync outcome.

### Trap / pause containment (E14 — required validation for C20)

1. Explore trap-dense areas with auto-pause-on-trap enabled until several trap discoveries occur.
2. Expected: `[TrapFix] Contained missing-IK reset` lines where NRE storms used to be, **zero**
   `ForceRotateToDesired` exception stacks, no `sceneEntities` fork in the pause window, and matching
   `[TrapDiag]` `(tick, unit, seq)` records across peers.
3. Comparison contract: records are keyed `(networkTick, UniqueId, seq)`; a throw on one peer against
   a successful breadcrumb at the same key on the other is the decisive signal (both-peers-throwing or
   bare call-count skew proves nothing by itself).

### Weather combat-exit predicate (E22 — open)

1. Fight and end a combat in a veil-affected weather area.
2. Collect both logs; diff the `[WeatherDiag]` lines: `pre/post combatStateChanged` predicate sets
   (veil, `profileOverriden`, `currentEffect`, both `TargetInclemency` values) and each
   `SetNewInclemency ctrl=weather|wind ... Weather=pre->post`.
3. The differing predicate (or an unequal call) identifies the mechanism; do not attempt fixes before
   this evidence.

### Tactician remainder (E18 — open)

1. Run a Tactician-archetype character through combats; collect both logs.
2. Diff `[TacticianDiag] momentum event ... delta=... remainderAfter=...` sequences; the first
   diverging remainder (with its tick and delta) localizes the origin.

### Dialogue guards (E11/E12 — regression check)

Open dialogues with random-variant cues and skill-check answers (companion banters qualify) in co-op;
expected: `[DialogRng]` guard lines once each, and no `DialogSystem` divergence in any episode dump.

### Departure hygiene (E20 — regression check)

Have one player leave mid-session: expected zero action-bar exception stacks in the departure window
and `[ActionBarFix] Active` once.

### Transfer stack

Launch from lobby: `[Transfer] Steam P2P path for N target(s)` then `P2P upload complete`; on any
failure a logged Photon fallback reason. Receiver side reports delivery and `fed to game=True`.

### Injected divergence smoke test (single machine)

The engine cheats `net_allow_one` + `net_desync` produce a real synthetic divergence; expected: the
full DesyncWatch episode (POTENTIAL → bucket attributions → SERIOUS + dumps). Note: cheats are
lockstep commands — in real sessions they must be registered on both clients.

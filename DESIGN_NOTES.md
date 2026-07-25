# Multiplayer Stability Design Notes

## Scope

Multiplayer Stability is a set of Harmony patches and diagnostics for Rogue Trader co-op. It works
at mod level, so it targets specific engine seams rather than replacing the multiplayer model.

The project does not assume that its patches are better than an engine-level solution. Owlcat has
access to the complete source, build pipeline, telemetry, and test environment. These patches are
useful as reproduced defects, narrow mitigations, and examples of where a local value can cross into
lockstep state.

The current source contains 23 components across 25 C# files. Harmony applies 45 patch classes
independently at startup.

## Working Model

Rogue Trader uses a deterministic simulation with hashed state and named RNG streams. The recurring
failure pattern in the captures reviewed for game build `1.6.1.514` is:

1. A value differs locally between peers.
2. That value changes a simulation branch, entity field, command, fact, or RNG draw.
3. The resulting state enters a synchronized hash.
4. The peers report a desync later, sometimes in a different subsystem from the original cause.

Confirmed local inputs include:

- camera and fog visibility;
- live view objects, bones, and renderer bounds;
- Unity physics and trigger callback order;
- UI preview units and dialogue inspection;
- frame-timed animation and weather work;
- caches populated by aiming or preview calls;
- exceptions after a simulation write but before command bookkeeping completes.

This is a working explanation for the captured defects, not a claim that every Rogue Trader desync
has the same cause.

## Design Rules

### Exact peer parity

Every simulation-changing component must be installed at the same version on every peer. A
one-sided deterministic change is still a deterministic disagreement. Version parity is manual in
v0.8.32; a session-latched compatibility check is planned for 0.9.

### Narrow patch scope

Patches target the caller or condition that was linked to a capture or confirmed in engine code.
The project avoids global RNG replacement, broad exception suppression, and command-buffer
rewrites.

### Solo behavior

Multiplayer behavior is normally gated so single-player follows vanilla code. Two older fixes are
solo-safe but not byte-identical:

- weather VFX draws use a non-hashed fallback;
- projectile aim-bone selection uses a fixed choice.

### No automatic resync

The mod can suppress the popup for a confirmed short transition flap, but it does not start a
resync. A persistent or broader mismatch restores the normal warning.

### Local failure handling

Patch classes are applied independently. A class that fails to resolve logs `[Init][ERR]` and does
not prevent unrelated classes from loading. Runtime guards generally return to the vanilla path at
their own call site.

This is best-effort isolation, not atomic component activation. A component that spans several
patch classes can be partly active if only one class fails. Startup errors therefore make the whole
build unsuitable for multiplayer until reviewed.

### Evidence labels

- **Field validated:** a post-fix paired capture exercised the original scenario without the
  previous divergence.
- **Mechanism confirmed:** logs or source establish the defect and intervention, but a dedicated
  post-fix capture is still missing.
- **Diagnostic:** records evidence and intentionally does not change simulation behavior.
- **Infrastructure:** transfer, loading, or reporting behavior rather than a desync prevention
  patch.

## Component Summary

| ID | Component | Source | Purpose | Status |
|---|---|---|---|---|
| C01 | Transfer booster | `TransferBooster.cs` | Pumps transfer acknowledgements and adapts the vanilla transfer window. | Infrastructure, measured |
| C02 | Steam save transfer | `SteamP2P.cs`, `SteamSaveTransfer.cs` | Moves save bytes through Steam Networking Messages with Photon control and fallback. | Infrastructure, measured |
| C03 | Desync watch | `DesyncWatch.cs` | Records episodes, state buckets, RNG fingerprints, entities, and transition-flap policy. | Diagnostic; flap policy field validated |
| C04 | Weather RNG | `WeatherRngFix.cs` | Keeps render-loop weather VFX draws out of the hashed Weather stream. | Field validated |
| C05 | Projectile RNG | `ProjectileRngFix.cs` | Removes view-dependent aim-bone draws from the hashed Projectiles stream. | Field validated |
| C06 | Sequenced locks | `SequencedLocks.cs` | Adds sequence identity to selected loading barriers. | Field validated |
| C07 | Deterministic sleep | `DeterministicSleep.cs` | Replaces camera-driven awake census decisions with synchronized policies. | Field validated |
| C08 | Fog gates | `FogGateFix.cs` | Removes local fog and render visibility from six mechanics decisions. | Partly field validated |
| C09 | Dash delivery | `DashDeliveryFix.cs` | Stops live view position from choosing dash delivery timing. | Mechanism confirmed |
| C10 | Preview ghosts | `PreviewGhostFix.cs` | Isolates preview entities from facts, aura candidates, item copies, and hashed UUID creation. | Mostly field validated |
| C11 | RNG leak detector | `LeakDetector.cs` | Reports hashed RNG draws outside simulation ticks. | Diagnostic, field validated |
| C12 | Preview rulebook guard | `PreviewRulebookGuard.cs` | Prevents preview subscribers from entering the global gameplay event bus. | Field validated |
| C13 | Local time scale | `LocalTimeScaleFix.cs` | Removes local visibility and pause-bind writes from hashed game time. | Mechanism confirmed |
| C14 | Deterministic order | `DeterministicOrderFix.cs` | Sorts physics range-query results before downstream random selection. | Mechanism confirmed |
| C15 | Projectile position | `ProjectilePositionFix.cs` | Uses entity geometry instead of local view bones for projectile mechanics. | Mechanism confirmed |
| C16 | Dialogue RNG | `DialogRngFix.cs` | Keeps UI inspection of answers and cues from advancing hashed dialogue RNG. | Field validated |
| C17 | Idle animation RNG | `IdleAnimationRngFix.cs` | Routes idle variety through the engine's non-hashed idle stream. | Field validated |
| C18 | Action-bar event guard | `ActionBarRoleSpamFix.cs` | Skips invalid unitless UI refreshes during player join and leave callbacks. | Field validated |
| C19 | Weather combat-exit diagnostic | `WeatherCombatExitDiag.cs` | Records predicates and Weather draws around combat-exit inclemency changes. | Diagnostic |
| C20 | Trap pause diagnostic and containment | `TrapPauseDiag.cs` | Records paused rotation calls and prevents a known null IK reset from aborting command processing. | Mechanism confirmed |
| C21 | Augmentation bark guard | `AugmentationBarkFix.cs` | Stops a client-only augmentation bark from writing hashed played-banter state. | Mechanism confirmed |
| C22 | Charge path fix | `ChargePathDiag.cs` | Disables target-blind partial charge-path cache reuse while retaining exact cache hits. | Mechanism confirmed |
| C23 | Tactician diagnostic | `TacticianDiag.cs` | Records momentum deltas and the hash-omitted Tactician remainder. | Diagnostic |

Each source header contains the exact Harmony target, engine behavior, intervention, gate, and
failure path. The table is an index, not a replacement for the code.

## Transfer Architecture

Photon remains the authoritative control path. When every peer advertises support, the host sends
bulk save data through Steam Networking Messages:

1. peers negotiate the side channel over Photon;
2. save bytes are sent sequentially to each target peer;
3. Steam may use a direct route, ICE, or Steam Datagram Relay;
4. Photon remains available as fallback;
5. the received byte array is passed back to the game's normal save consumer.

The measured improvement in current captures is about 8x over the vanilla Photon bulk path.

Known hardening gaps:

- control event code 100 has no protocol magic, version, transfer ID, or checksum;
- completion can be acknowledged before the game accepts the byte array;
- fallback is not tracked per peer after partial multi-peer success.

These are 0.9 work items. They are not claims that the current transfer path caused a captured
simulation desync.

## Behavioral Differences

The following differences are deliberate:

- hidden AI turns run at 1x in co-op;
- ambient scene loops farther than 25 metres from the party can sleep until approached;
- projectiles use the target entity's base point for mechanical geometry;
- the augmentation screen does not play its random bark in co-op;
- a dialogue cue shown during UI inspection can differ from the synchronized cue chosen when the
  dialogue advances;
- confirmed short transition-only RNG flaps do not show the desync dialog unless they persist or
  spread to another state bucket.

## Open Work

### Instrumented but not fixed

- Weather combat exit: a visual profile override can gate hashed Weather draws and player fields.
  The differing predicate has not been isolated.
- Tactician remainder: `MomentumThisCombat` is omitted from its component hash. The first source of
  the remainder split remains unknown.

### Additional investigations

- area-effect candidate sets populated by local Unity trigger callbacks;
- a single-entity Eogann fork with matching recorded streams and creation history;
- `UnitFollowUnitController.ShouldAct` reading local movement-view state before scheduling commands;
- dodge and special-attack animation variant picks that share the idle animation RNG property.

### Post-fix validation still required

- charge, attack, and parry with the attacker and target ending on the same tile;
- trap detection under repeated auto-pause;
- augmentation-screen bark containment;
- preview item copy scope;
- newer fog-gate call sites;
- dash delivery, time scale, physics ordering, and projectile geometry.

## Planned 0.9 Hardening

The planned 0.9 series changes the operational envelope rather than adding more gameplay patches:

1. session-latched exact-version compatibility and component status reporting;
2. framed and validated Steam transfer messages with parser tests;
3. delivery ACK/NACK tied to game acceptance and per-peer fallback state;
4. retry, timeout, and abort behavior for sequenced loading barriers;
5. tick-checked desync attribution, per-call-site leak accounting, and a guarded reflection audit.

New gameplay fixes should remain evidence-driven and separate from this hardening series.

## Source Layout

The repository root contains documentation and license files. Unity source is isolated under:

`Assets/Modifications/MultiplayerStability`

Only files imported by Unity carry `.meta` files. Root documentation does not need Unity metadata.
The build-generated `Generated` folder is ignored.

The important entry points are:

- `Scripts/MultiplayerStabilityMain.cs`: Harmony initialization and patch-class isolation;
- `Scripts/DesyncWatch.cs`: state attribution and player warning policy;
- `Scripts/SteamP2P.cs` and `Scripts/SteamSaveTransfer.cs`: save-transfer side channel;
- individual component files: patch targets and local rationale.

## Testing and Maintenance

For multiplayer captures:

1. collect `GameLogFull.txt` from every peer;
2. identify the first mismatch tick on each machine;
3. compare state buckets, RNG fingerprints, entity hashes, and component diagnostics;
4. distinguish the first divergent write from later hash fallout;
5. only mark a fix field validated after the original scenario is exercised again.

After every Rogue Trader update:

1. rebuild against the updated Owlcat template and reference assemblies;
2. review every Harmony target and reflective member lookup;
3. compare the deterministic sleep replacement with the updated vanilla census;
4. run a multiplayer startup check on every peer;
5. repeat high-risk scenarios before publishing compatibility.

Historical paired logs and detailed capture notes are retained outside this repository because they
can contain account identifiers and routinely exceed normal source-repository size. Redacted
evidence can be provided for technical review.

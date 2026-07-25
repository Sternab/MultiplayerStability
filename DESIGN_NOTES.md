# Multiplayer Stability Design Notes

## Scope

Multiplayer Stability is a set of Harmony patches for Rogue Trader co-op. It targets specific
engine seams that were identified through paired logs and code review. It does not replace the
network model and does not claim to be an engine-level solution.

Owlcat has access to the complete source, build pipeline, telemetry, and test environment. This
project is useful as a set of reproduced defects, narrow mitigations, diagnostics, and test cases.

The v0.9.1 source contains 24 components across 26 C# files.

## Working Model

Rogue Trader runs synchronized simulation state with per-tick hashes and named stateful RNG streams.
The recurring defect pattern observed on game build `1.6.1.514` is:

1. A value differs locally between peers.
2. That value changes a simulation branch, command, field, fact, candidate set, or RNG draw.
3. The result enters synchronized state.
4. The game reports the mismatch later, sometimes under a different subsystem.

Confirmed local inputs include camera and fog visibility, live view objects and bones, renderer
bounds, Unity physics order, UI preview work, frame-timed VFX, and cache contents. Exceptions can
also fork state when they occur after a simulation write but before command bookkeeping completes.

This model explains the defects listed below. It is not a claim that every desync has one cause.

## Safety Envelope

### Shared compatibility decision

Lobby mod properties are local observations, not a consensus protocol. v0.9.1 therefore makes one
save-sender decision at each save-transfer epoch:

1. Every peer reads the Photon mod properties and exchanges its compiled assembly MVID with every
   other peer.
2. The peer uploading the save checks manifest version and module identity, then hashes the sorted
   actor roster.
3. It sends a reliable `MPSC` decision directly to every other actor before vanilla sends
   `LoadSave`.
4. Downloading peers validate the sender, version, module identity, player count, and roster hash
   before enabling simulation fixes or custom protocols.

Pending decisions are keyed by actor because vanilla permits non-owners to start a save and resolves
simultaneous starts by actor number. An incompatible decision disables those behaviors on 0.9 peers.
If the save sender cannot queue the
decision, it aborts the start attempt. If a client sees the mod on another peer but receives no
valid decision, it refuses the load. This avoids entering play with different policies.

Pre-0.9 builds do not understand the decision frame. Mixed sessions containing those builds remain
unsupported. Exact version parity also does not prove that every Harmony class installed, so any
startup `[ERR]` invalidates the build for multiplayer.

Diagnostics, UI-only guards, and the transfer acknowledgement pump do not depend on the simulation
gate. Solo or unresolved sessions use vanilla simulation paths.

### Patch isolation

Harmony classes are applied independently. Failure of one class does not stop unrelated classes,
but a component spanning several classes can be left partly active. Runtime behavior is local to
each patch:

- most prevention patches return to vanilla before mutation when their guard or reflection fails;
- diagnostics are designed not to throw into gameplay;
- the compatibility gate fails closed when a shared policy cannot be established;
- the trap containment separates known null state, reflection drift, logging failure, and real
  target exceptions instead of handling them through one broad catch.

### Player control

The mod never starts a resync automatically. DesyncWatch records transition context but does not
suppress or replace the game's desync dialog.

### Evidence labels

- **Field evidence:** paired post-fix logs exercised the relevant path without the previous fork.
- **Mechanism confirmed:** code and captures identify the path, but a dedicated post-fix session is
  still required.
- **Diagnostic:** records evidence and intentionally does not change simulation.
- **Infrastructure:** changes transfer, loading, compatibility, or reporting behavior.

## Components

| ID | Component | Source | Purpose | Evidence |
|---|---|---|---|---|
| C01 | Compatibility gate | `MultiplayerCompatibility.cs` | Exchanges build identity all-to-all and distributes a save-sender decision for exact-build activation. | 0.9.0 failure reproduced; 0.9.1 field pending |
| C02 | Transfer booster | `TransferBooster.cs` | Pumps Photon acknowledgements, gates the larger window, and resets leases by session generation. | Prior transfer captures; 0.9.1 gate pending |
| C03 | Steam save transfer | `SteamP2P.cs`, `SteamSaveTransfer.cs` | Moves validated bulk save bytes through Steam with per-peer fallback and replayable completion. | Prior path measured; 0.9.1 wire pending |
| C04 | Desync watch | `DesyncWatch.cs` | Records episodes, buckets, RNG state, entity hashes, transition context, and tags upstream telemetry. | Diagnostic used in paired captures |
| C05 | Weather RNG | `WeatherRngFix.cs` | Keeps render-loop VFX draws out of the hashed Weather stream. | Field evidence |
| C06 | Projectile RNG | `ProjectileRngFix.cs` | Removes view-dependent aim-bone draws from the hashed Projectiles stream. | Field evidence |
| C07 | Sequenced locks | `SequencedLocks.cs` | Adds identity, retry, timeout, abort, and progress reporting to selected loading barriers. | Prior sequencing evidence; v0.9.1 retry pending |
| C08 | Deterministic sleep | `DeterministicSleep.cs` | Replaces local camera census decisions for combat-relevant units and stabilizes corpse state. | Field and performance evidence |
| C09 | Fog gates | `FogGateFix.cs` | Removes local fog or render visibility terms from six mechanics call sites. | Partial field evidence |
| C10 | Dash delivery | `DashDeliveryFix.cs` | Defers target delivery until the synchronized charge endpoint is established. | Mechanism confirmed |
| C11 | Preview ghosts | `PreviewGhostFix.cs` | Isolates preview entities from facts, auras, item-copy UUID draws, and gameplay state. | Multiple field captures |
| C12 | RNG leak detector | `LeakDetector.cs` | Reports hashed RNG draws outside simulation ticks, capped per stream and call site. | Diagnostic used in captures |
| C13 | Preview rulebook guard | `PreviewRulebookGuard.cs` | Blocks preview owners from the global gameplay event bus. | Field evidence |
| C14 | Local time scale | `LocalTimeScaleFix.cs` | Removes local visibility and pause-bind writes from hashed game time. | Mechanism confirmed |
| C15 | Deterministic order | `DeterministicOrderFix.cs` | Sorts equal-membership range results before downstream selection. | Mechanism confirmed |
| C16 | Projectile position | `ProjectilePositionFix.cs` | Uses entity geometry instead of local view bones for projectile mechanics. | Mechanism confirmed; limited field coverage |
| C17 | Dialogue RNG | `DialogRngFix.cs` | Isolates preview getters and omits a mutating nested-answer UI query in exact-parity multiplayer. | Leak field evidence; no-side-effect policy pending |
| C18 | Idle animation RNG | `IdleAnimationRngFix.cs` | Routes idle variety through the engine's non-hashed idle stream. | Field evidence |
| C19 | Action-bar guard | `ActionBarRoleSpamFix.cs` | Skips invalid unitless UI refreshes during join and leave callbacks. | Field evidence |
| C20 | Weather combat-exit diagnostic | `WeatherCombatExitDiag.cs` | Records the predicates and Weather draws around combat-exit inclemency. | Diagnostic |
| C21 | Trap diagnostic and containment | `TrapPauseDiag.cs` | Records paused rotation calls and contains the confirmed null IK reset. | Mechanism confirmed; post-fix field pending |
| C22 | Augmentation bark guard | `AugmentationBarkFix.cs` | Stops a client-only bark from writing hashed played-banter state. | Mechanism confirmed |
| C23 | Charge path fix | `ChargePathDiag.cs` | Disables target-blind partial charge cache reuse while retaining exact hits. | Source confirmed; Dark Heresy corroboration; field pending |
| C24 | Tactician diagnostic | `TacticianDiag.cs` | Records momentum deltas and the hash-omitted Tactician remainder without creating component data. | Diagnostic |

Each source header documents its Harmony target, reason for patching, activation gate, and failure
behavior. The table is an index, not a substitute for the implementation.

## Network Changes

### Compatibility frame

Build reports use Photon code 100 with `MPSH` magic, a protocol version, manifest version, and
assembly MVID. Reports are exchanged all-to-all so any vanilla-authorized save sender can evaluate
the roster. The compatibility decision uses the same code with `MPSC` magic plus the sender's
decision, version, module identity, player count, and roster hash. It is sent directly per actor
before `CloseRoom`; the engine's broadcast target list is empty before that point. Payloads without
either magic pass through to other handlers.

### Steam save transport

Photon remains the control path. Only the packed save byte array can use Steam Networking Messages.
The v0.9 protocol includes:

- `MPST` magic and protocol version;
- transfer IDs on control and data frames;
- a 512 MiB receiver bound;
- exact ordered offsets and declared length;
- SHA-256 validation;
- completion only after the current `SaveNetManager` download task accepts the bytes;
- one accepted-transfer tombstone per sender so a repeated query can replay completion after
  vanilla unregisters the type-24 receiver;
- NACK, cancellation, idle timeout, and per-peer Photon fallback.

Steam may use a direct route or Steam Datagram Relay. The mod does not assume that a direct IP
connection is exposed. Prior captures measured roughly 8x faster transfer than the vanilla bulk
path, but route, save size, and connection quality affect the result.

While waiting for completion, the sender repeats the original query every ten seconds. A receiver
that already accepted the bytes replays COMPLETE from its bounded tombstone, including after
`DownloadSave` has finished repacking and removed vanilla's type-24 receiver.

### Sequenced loading barriers

The mod extends selected code-8 lock frames with magic, protocol version, lock point, and sequence.
Reach messages retry once per second. A 30-second timeout sends an abort and returns that client to
the vanilla barrier path. This prevents a permanent local wait, but it is not a distributed
consensus protocol and still depends on Photon's reliable event path.

## Deliberate Behavior Changes

- Hidden AI turns run at 1x in multiplayer.
- Combat-capable units use a synchronized sleep policy; distant ambient units retain the current
  vanilla camera policy.
- Finally-dead units use `IsDeathRevealed = true` in multiplayer.
- Projectile mechanics use entity-derived target geometry instead of live view bones.
- The augmentation screen does not play its client-random bark in multiplayer.
- The nested-answer "new answers" marker is omitted in exact-parity multiplayer. The underlying
  query can trigger and persist a party skill check; synchronized answer selection is unchanged.

## Open Work

### Instrumented, not fixed

- Combat-exit weather selection can gate hashed Weather draws on a visual profile override.
- `MomentumThisCombat` is omitted from the Tactician component hash. The first source of its
  remainder split is not yet proven.

### Structural limits

- Unity physics and trigger callbacks can produce different candidate membership. Sorting only
  repairs order when membership already matches.
- Dash delivery no longer reads live view position, but movement-agent completion can still choose
  a different tick.
- The deterministic sleep ambient branch intentionally retains local camera behavior for units that
  cannot currently join combat.
- Projectile base geometry is conservative and starship behavior has limited field coverage.
- `Rand.Get` and the private global rulebook subscribe overload are small JIT targets. They are patched
  during initialization, but runtime evidence is still required after engine or runtime changes.
- Patch target names, reflected fields, and replicated current-build predicates require review
  after every game update.

## Dark Heresy Comparison

The supplied Dark Heresy beta decompile was used as a comparison, not as proof of Rogue Trader
intent or final Dark Heresy behavior.

| Area | Dark Heresy observation | Relevance |
|---|---|---|
| Charge path cache | The target-blind partial cache path is absent; exact hits include destination and target identity. | Strong independent support for C23 |
| Sleeping units | Timer aging is more explicit and includes tick-based wake state, but camera and fog still affect sleep and corpse reveal state. | Useful lifecycle design, not a deterministic census fix |
| Loading locks | The one-byte lock implementation is materially unchanged. | Supports the same seam; no upstream resolution observed |
| Projectile aim | View-gated `ParticlesSnapMap` selection and hashed projectile RNG remain. | The core C06 risk is still present in the beta snapshot |
| Dash delivery | Delivery still reads `movementAgent.IsReallyMoving`. | The local completion-tick risk remains |
| Range ordering | `FindUnitsInRange` remains unsorted while the shape-query sibling sorts. | Supports C15 but does not solve membership differences |
| Fog mechanics | Local fog reads remain in combat join, awareness, and area-effect paths. | No broad replacement for C09 was found |
| Tactician | The old component is marked obsolete and its momentum rule handler is removed. | Subsystem retirement, not a portable Rogue Trader patch |

The comparison gives direct corroboration for the charge cache change and several maintenance
clues. It does not support a claim that the sequel broadly fixed lockstep desynchronization.

## Testing and Maintenance

v0.9.1 has been compiled against the Rogue Trader `1.6.1.514` reference set. The aggregate review
build still requires a two-sided multiplayer session.

For field captures:

1. Confirm clean startup and `[Compat] Compatible` on every peer.
2. Collect `GameLogFull.txt` from every machine.
3. Identify the first mismatch tick rather than the largest later dump.
4. Compare bucket attribution, full RNG state, entity hashes, and component diagnostics.
5. Mark a fix field-tested only after the original scenario is exercised again.

After a game update:

1. extract the modification template shipped with that game build and refresh the editor reference
   assemblies before rebuilding;
2. review every Harmony target and reflected member;
3. compare the sleep replacement with the current engine method;
4. run startup and save-transfer tests on every peer;
5. repeat charge, trap, dialogue, weather, projectile, and dense-area performance scenarios.

Raw captures and decompiled game code are intentionally excluded from the repository. Logs can
contain account identifiers and are often hundreds of megabytes.

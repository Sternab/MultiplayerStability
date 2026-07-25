# MultiplayerStability Plan and Status

This document began as a design plan on 2026-07-02. It was reconciled on 2026-07-09 and last
updated on 2026-07-24 against manifest **v0.8.32** and the current source. It now records shipped
behavior and planned work. The mod is a standalone OwlcatModification and Harmony project under
`Assets/Modifications/MultiplayerStability` in the Owlcat modification template. Author: Sternab.

Rogue Trader co-op is deterministic lockstep (50 ms ticks, only player *commands* cross the wire via Photon).
Any client-local difference that reaches the simulation can fork the per-tick state hash and cause a
desync. The mod adds attribution, removes confirmed causes where a Harmony patch is appropriate, and
improves save-transfer throughput. Players retain control over resync.

## Design rules

- **No automatic resync.** This reverses the original plan's `AutoResync` phase. Recovery stays under
  player control. The mod can show the vanilla resync dialog again for a confirmed serious desync,
  but it never calls `StartGameWithoutSave` itself.
- **Solo-safe (not always byte-identical).** Most behaviour-changing components self-gate on
  `NetworkingManager.IsMultiplayer` or run only through co-op-only seams, so solo is byte-identical there. Two
  are always-on: WeatherRngFix (weather VFX draw from the non-hashed fallback: no gameplay effect) and
  ProjectileRngFix (deterministic aim-bone pick, which feeds ricochet/push geometry: a low-impact solo change).
  Their solo differences are cosmetic, but solo execution is not strictly byte-identical to vanilla.
- **Best-effort fail-open.** Patching is isolated per patch class: a failing class logs `[Init][ERR]` and stays
  inert while the others continue, so a component built from several classes can be left partially active;
  runtime guards fall back to the vanilla path at their own site. Patch failures should not disable an
  unrelated code path or introduce state divergence.
- **Mod parity: session-latched compatibility gate planned for 0.9.** The design does not block launch. If every peer has
  the exact compatible build, simulation fixes enable; otherwise every modded peer stays on **vanilla** behavior
  and logs one clear warning. Latched per simulation epoch (initial launch / save-transfer relaunch: a joiner
  is accounted for before `PlayersReadyMask`), never reevaluated per patch call, never flipped mid-session.
  Diagnostics and UI fixes stay active regardless; deterministic simulation changes share the gate. Until 0.9
  ships, parity remains manual: match exact versions on every machine.
- **Peer-compatibility categories:** component entries use these three categories:
  - *Subset-safe*: diagnostics (DesyncWatch, LeakDetector), UI-only fixes (ActionBarRoleSpamFix), the transfer
    ack-pump. Safe on any subset of machines.
  - *Negotiated protocol*: Steam P2P transfer, Sequenced Locks, the Photon window boost: self-gate on
    every-peer-modded and engage only when all peers participate.
  - *Exact parity required (until the 0.9 latch)*: **every** RNG- or simulation-changing prevention fix
    (Weather, Projectile RNG + Position, Deterministic Sleep incl. corpse/fader, all FogGate sites, Dash,
    both preview-ghost halves, Local Time-Scale, Deterministic Order, Dialog, Idle Animation). Mixed installs
    range from ineffective (Weather: no worse than vanilla, but fixes nothing) to actively
    desync-causing (Deterministic Order: one-sided sorting guarantees order disagreement). Treat all
    of them as requiring
    identical builds on every machine.

## Shipped components (v0.8.32)

Entry point: `MultiplayerStabilityMain.Initialize`. Patching is isolated per class. Since v0.8.12, each
`[HarmonyPatch]` class is patched in its own `try`/`catch`; one failing class logs `[Init][ERR]` and stays inert
instead of aborting init; a blanket `PatchAll` previously let one throw kill everything after it, including
the transfer stack. Isolated `Wire()` calls then run in this order: `SteamSaveTransfer`, `DesyncWatch`, `WeatherRngFix`,
`LeakDetector`, `PreviewRulebookGuard`. 23 numbered components across 25 `.cs` files (v0.8.x added
`LocalTimeScaleFix.cs`, `DeterministicOrderFix.cs`, `ProjectilePositionFix.cs`, `DialogRngFix.cs`, `IdleAnimationRngFix.cs`, `ActionBarRoleSpamFix.cs`).

### Transfer and loading
- **Transfer Booster** (`TransferBooster.cs`): vanilla throttles co-op save transfer to ~0.22 MB/s (Photon's
  send window only refills on acks, and acks go once per rendered frame during low-FPS loading). Two levers via
  Prefix+**Finalizer** on `SaveNetManager.UploadSave/DownloadSave`: an 8 ms `SendAcksOnly` ack-pump (every
  transfer, subset-safe) and a 96 KBx4 chunk/window boost (**only when every player runs the mod**: a fast
  sender without fast receiver acks gets force-disconnected by the relay). Refcounted, vanilla values restored
  at count 0. *Ack-pump: subset-safe. Window boost: negotiated protocol.*
- **Steam P2P save transfer** (`SteamP2P.cs` + `SteamSaveTransfer.cs`): routes the ~16 MB save payload over a
  direct Steam `SteamNetworkingMessages` channel (ICE NAT-punch + SDR fallback) instead of the ~230 KB/s Photon
  relay; **~8x measured speedup**. Photon stays the control plane; a code-100 handshake negotiates, the receiver
  completes the real `m_DownloadSaveTcs`. Mod-side AIMD rate control drives `SendRateMin`. Prefixes on
  `DataTransporter.SendSave` + `MessageNetManager.OnMessage`. *Both-Steam + both-modded, else transparent Photon
  fallback on any failure or timeout. The current performance result is recorded in
  `SAVE-TRANSFER-SPEED.md`.*
- **Sequenced Locks** (`SequencedLocks.cs`): fixes the vanilla loading-barrier race (one reused `NetLockPointId`
  with no sequence number -> a fast client's next-barrier announcement is swallowed into the slow client's current
  barrier, hanging it at 100%). Tags each barrier with a per-session ordinal. Prefix `LockNetManager.Lock`/
  `OnLockReceived`; baseline reset on room leave + save upload/download. *Self-gated on `AllPlayersModded()` (wire
  format change); solo/mixed lobby = pure vanilla.*

### Diagnosis
- **Trap/Pause Diagnostic** (`TrapPauseDiag.cs`, v0.8.20: capture 0.8.19, two independent `sceneEntities`
  episodes): log-only instrumentation for the open trap/pause command-lifecycle class. Trap detection
  auto-pauses; `OnRun`'s paused `ForceLookAt` -> `ForceRotateToDesired` writes **sim** orientation then touches
  client-local View/IK (`View.IsVisible`-gated `GrounderIk.ResetPosition`); a one-machine throw there skips
  `DidRun` -> command bookkeeping diverges (party members only, RNG identical; the same exception pattern
  appears in 0.6.4 logs). Paused-window breadcrumbs reset their budget at the accepted
  `HandleGameModeChanged` -> Pause transition, not the rejectable `StartMode` request. Tick regression
  provides a save/load fallback. All exceptions are logged and rethrown unchanged. Every paused-window
  line carries a per-tick, per-unit dictionary ordinal. The two-sided comparison uses
  `(tick, unit, seq)` keys; a throw on one peer is compared with a successful record at the same key
  on the other. Bare exception counts do not establish the mechanism.
  **Containment shipped in v0.8.26-0.8.28** (4.5-hour capture: 72-vs-10 NREs,
  514-vs-107 residual exceptions, and forks limited to command-touched units). The shared NRE aborted
  the command batch mid-tick. The paused visible-unit IK reset
  is null-safe in MP. Logging is best effort; missing reflected members disable containment and restore
  vanilla behavior; the orientation FieldRef resolves in `Prepare()`; and a real reset exception is
  rethrown once without rerunning the method. The diagnostic remains enabled to validate the containment.
  Related audit item:
  `UnitFollowUnitController.ShouldAct` schedules sim commands off `View.MovementAgent.WantsToMove` (Channel B,
  can diverge with no exception). *Diagnostic half: log-only, subset-safe. Containment half (v0.8.26+):
  simulation-changing: exact parity required.*
- **Weather Combat-Exit Diagnostic** (`WeatherCombatExitDiag.cs`, v0.8.18: capture 0.8.17-SECOND): log-only
  instrumentation for the open second weather class. At combat exit, `HandlePartyCombatStateChanged` +
  `SetNewInclemency` draw hashed `Weather` and write hashed player fields, steered by the *visual*
  `IsProfileOverriden` flag (Channel B on the sim path). One client drew once more -> `player` bucket forked,
  then `randomState`, persistent. Logs the full gating input set (both controllers'
  `TargetInclemency` values used at source lines 347 and 351, veil, profile override, and
  `CurrentWeatherEffect` via field reflection) and each `SetNewInclemency` call with weather-vs-wind attribution (`m_WeatherData` reference
  identity) and bracketing pre->post fingerprints; the next two-sided capture with combat near weather names
  the differing predicate. **Diagnostic only:** a `DisableStatefulRandomContext` wrap would write
  client-random values into hashed fields. `Owlcat.Runtime.Visual` types are accessed through reflection
  because that assembly is not referenced. *Log-only;
  subset-safe.*
- **Action-Bar Role Spam Fix** (`ActionBarRoleSpamFix.cs`, v0.8.14: tester 600 MB-log incident): on
  player-leave the engine raises a role event per controlled entity (~1,500) and `ActionBarSlotVM.HandleRoleSet`
  ignores its `entityId` parameter, refreshing every slot on every event and NRE-ing on unitless slots:
  ~18,000 full exception stacks in seconds into an uncapped `GameLogFull` (`LogSinkFactory` passes
  `int.MaxValue`). Prefix filters each slot to its **own** unit's events and skips unitless slots: vanilla
  behavior preserved exactly for the one event that matters per slot; v0.8.16 extends the unitless skip to the
  per-player room events (`HandlePlayerEnteredRoom`/`LeftRoom`, ~155 of the 425 residual stacks in the 0.8.14
  capture); v0.8.17 removes the `IsMultiplayer` gate from both guards: instantaneous `PlayerCount > 1` goes
  false *before* 2->1 departure callbacks, so the gate disabled the guard during the highest-volume window
  (~270 stacks); ungated because the filtering invariant is valid in every context, including teardown and
  rare non-departure raisers (`net_allow_one`, `PlayerRole.ForceSet`), so no player-count test is required. Deliberately
  *not* a global exception suppressor (surrounding network context stays loggable). *Ungated; UI-only;
  subset-safe.*
  **Operations:** zip capture logs and retain only incident sessions. A dedicated rotating MP log
  (~25-50 MB x2) remains an option if another high-volume exception class appears.
- **DesyncWatch** (`DesyncWatch.cs`): makes desyncs visible/attributable, never auto-resyncs. Per-episode log
  with inferred tick%5 bucket (player / sceneEntities / areaPersistent / randomState / syncData+signals);
  ring buffers for RNG-stream post-tick fingerprints, GlobalUuid entity/fact-creation names, and local tick
  hashes (two machines' logs identify the affected stream/entity); per-entity `sceneEntities` hash dump on serious
  desyncs; re-arms the once-per-session `WasDesync` latch on recovery. **Transition-flap policy:** a prefix on
  `UIDesyncHandler.RaiseDesync` suppresses the vanilla resync dialog for a *confirmed randomState-only* desync
  occurring in a loading/cutscene window, re-showing it only if the episode graduates to another bucket or
  persists (~10 s). Six patches + one handler injection. *Log-only; subset-safe.*
- **Leak Detector** (`LeakDetector.cs`): a prefix on `Rand.Get()` (the universal chokepoint for every
  hashed RNG draw + uuid mint) logs any draw of a serializable/hashed stream that fires *outside* a deterministic
  sim tick. It identifies the call site from **one machine without requiring a desync** and also works solo. Log-only (no
  automatic diversion: the fallback is non-deterministic, so diverting a false positive would create a desync).
  *Blind spots (inherent): off-thread/Pathfinding draws, and Channel-B "view flag read by mechanics" leaks that
  never pass through `Rand.Get`.*

### Prevention patches
- **Charge-Path Fix + Diagnostic** (`ChargePathDiag.cs`, fix v0.8.30: tester same-tile report + decompile
  mechanism + Dark Heresy corroboration): RT's partial charge-path cache lookup matches
  caster, origin, and `ignoreBlockers` only (no destination key and **no target**) and cuts a cached,
  potentially preview-polluted path at the destination node. Delivery writes `Caster.Position` to that node
  unconditionally, producing the reported same-tile state. In MP, partial reuse is disabled. Exact target-checked
  hits remain cached and unmatched paths recompute, matching the newer Dark Heresy implementation. The
  resolution diagnostic remains enabled as a validator. Verification requires no `Partial_Patch` init error,
  `[ChargeFix] Active`, and no `partial-cache` lines. Silence alone is inconclusive. *MP-gated; exact
  parity required.*
- **Tactician Diagnostic** (`TacticianDiag.cs`, v0.8.29, log-only): every momentum event logged with owner,
  delta, and post-event `MomentumThisCombat` remainder. The accumulator is **omitted from its part hash**, so
  remainder divergence is invisible until one peer crosses 100 and mints a one-sided buff. Origin of the
  split unproven (possibly the now-fixed charge path); same base-only-hash mistake in `MomentumReachedTrigger`,
  `HunterDodge`, `ChangeVeilDamage`: bounded hash audit queued. *Log-only; subset-safe.*
- **Augmentation Bark Fix** (`AugmentationBarkFix.cs`, v0.8.24: capture 0.8.23 `player`-bucket fork): the
  client-local augmentation screen picked a bark via `UnityEngine.Random` and its handler wrote hashed
  `Player.PlayedBanters` only on the peer that opened the screen. A caller-scoped flag wraps the
  `AugmentationsVM` constructor, and `HandleBarkBanter` skips while the flag is set in MP. Simulation-side
  raisers (etude `ShowBanter` and system-map objects) remain unchanged. Cost: no augmentation-screen
  bark in co-op. *MP-gated; exact parity required.*
- **Weather RNG Fix** (`WeatherRngFix.cs`): wraps `VFXWeatherSystem.Update` in `DisableStatefulRandomContext`
  (Prefix + **Finalizer**) so render-frame weather VFX stop draining the hashed `Weather` stream. The wrap covers
  the whole `Update -> UpdateWeather -> UpdateAllControllers` chain, so the per-controller spawn/lightning draws
  (`WeatherMinMaxRateSpawnController.Update`, `WeatherLightningBoltController.Spawn`) run nested inside it and are
  already covered. No separate patch is needed. *Not MP-gated (benign in solo: the wrapped draws are pure VFX).*
- **Projectile RNG Fix** (`ProjectileRngFix.cs`): transpiler on `Projectile.BeforeLaunch` swaps the
  view-conditional `Random<FxBone>` draw (fires only when the target View has a `ParticlesSnapMap` = client-local)
  for a deterministic first-locator pick. *Exact parity required.*
- **Projectile Position Fix** (`ProjectilePositionFix.cs`, v0.8.7, identified through the Solasta comparison):
  in MP, `Projectile.GetTargetPoint` and the attack-line random-locator take the
  engine's own deterministic **no-view fallbacks** (entity/grid point + miss offset; ship position + up; grid
  node + 1 m) instead of live view-bone transforms. This addresses the ricochet and push geometry path left
  by the RNG fix (SnapMap-present vs SnapMap-absent clients computed different mechanics with identical RNG),
  covers the
  long-open `GetTargetPointForStarship` sibling and its conditional hashed hull-point draw, and removes the
  view-gated `UnitLogic.Abilities` stream draw in `TryGetTargetPointByRandomLocator`. Visual cost in MP:
  projectiles aim at the target's base point, identical to vanilla's own SnapMap-less behavior. *MP-gated;
  exact parity required.*
- **Dialog RNG Fix** (`DialogRngFix.cs`, v0.8.10: two-sided capture 0.8.8): the
  dialogue UI's answer-preview getters (`BlueprintAnswer.SkillChecks` / `SkillChecksDC`) run at view time and
  draw the hashed `DialogSystem` stream (`SkillChecksDC` -> `CharacterSelection.SelectUnit(Random)`; `SkillChecks`
  -> `CueSelection.Select(Random)`). Preview refresh frequency is client-local, so these calls fork `randomState`
  mid-conversation. Two timing-independent guards: **(A)** both preview getters hold
  `DisableStatefulRandomContext` for their whole body in MP (semantic: preview-by-definition, valid even when
  executing inside a sim tick); the real `SelectAnswer` acting-unit pick keeps its synced hashed draw.
  **Guard C (v0.8.20):** the third confirmed view-time caller: `DialogController.HasNextUnselectedAnswers`
  (answer-tree inspection, sole external caller `AnswerVM`; the Solomorne fork was reported by
  LeakDetector) receives the same semantic whole-body wrap.
  Guard B, a deterministic first-eligible cue replacement, was **removed in v0.8.15** because
  `CueSelection.Select` also serves real narrative progression. The semantic preview wrap covers the
  confirmed leak without changing story selection. *MP-gated; exact parity required.* Capture 0.8.8 also
  disproved the `IsSimulationTick` timing gate because preview work can run inside a tick.
- **Idle Animation RNG Fix** (`IdleAnimationRngFix.cs`, v0.8.11: three-machine capture 0.8.10):
  `AnimationManager.StatefulRandom` maps idle-variety draws
  (micro/variant idle triggers, speed jitter, retrigger trackers) to the *hashed* `Animation3` stream on the
  view/animation clock, so idle timing skews `randomState` transiently (the source of most
  transition-window noise) and can escalate to serious when draws recur while skewed. The engine's own
  `PFStatefulRandom.Visuals.AnimationIdle` is the designated **non-hashed** idle stream (explicitly excluded
  from serialization). In MP, a transpiler swaps the hashed property read in the
  `get_StatefulRandom` read in the four idle sites (`TickIdleVariants`, `OnAnimationSetChanged`,
  `MicroIdle.OnStart`, `VariantIdle.OnStart`) for `AnimationIdle`; idle variety stays random per-machine (pure
  view), the hashed stream stops moving. Dodge/special-attack variant picks share the property but fire inside
  synced combat execution and remain unchanged pending their own audit. *MP-gated; exact parity required.*
- **Deterministic Sleep** (`DeterministicSleep.cs`): a replacing prefix on
  `SleepingUnitsController.Tick` computes one final verdict per unit. The earlier postfix design
  double-wrote disagreeing units, causing repeated view GameObject activation and the Thassera FPS
  regression. The replacement makes the awake census deterministic for the simulation paths that
  depend on it (death timing, ability counts, turn and attack-of-opportunity triggers, and combat join).
  **Census policy (v0.8.x):** deterministic verdict in combat AND, in peaceful play, for every *combat-capable*
  unit (the engine's own `CanJoinCombat` predicate: combat starts are decided on the previous tick's census, so
  those verdicts must be deterministic before the fight exists); ambient units that can never join combat keep the
  vanilla camera verdict (bridge-perf carve-out). Always-awake invariants: dying units, `Sleepless` units
  (vanilla parity), cutscene-held units near the party (synced distance), starships. Also writes corpses'
  hashed `IsDeathRevealed` = **true** for finally-dead units. In v0.8.15, the earlier frustum term
  was removed because `IsInCameraFrustum` tests local renderer bounds and is not deterministic.
  Corpse-heavy maps may keep more views active. The component also cancels the fog-dissolve fader's sim-side
  `Wake` in MP (`EntityFader` patch in the same file). Two-pass compute-then-apply so *compute-phase* failures leave the
  vanilla census untouched (a failure in the trivial apply pass could still leave partial state: never
  observed; the apply path is list ops and property sets). *MP-gated; exact parity required.*
- **Fog Gate Fix** (`FogGateFix.cs`): transpiler dropping a client-local visibility term from the **six**
  mechanics decisions that consume one: `AreaEffectEntity.ShouldUnitBeInside` (aura membership),
  `UnitCombatJoinController.ShouldStartCombat` (NPC combat start), `LOSGetter.GetBaseValue` (swaps
  `IsVisibleForPlayer`->visible, keeping the synced `IsInGame` term; `HasLOS` stays the real gate),
  `UnitMovementAgentBase.TickMovement` (fog-gated 8x speed + heading snap: hashed positions),
  `PartyAwarenessController.Tick` (fog-gated awareness rolls/XP/trap triggers), and
  `RicochetHelper.GetPossibleRicochetTargets` (fog-filtered ricochet candidates). Internal types resolved by
  name. (The fog-gated 16x AI-turn time-scale uses a different policy: see Local Time-Scale Fix.)
  *Exact parity required.*
- **Dash Delivery Fix** (`DashDeliveryFix.cs`): prefix on `AbilityCustomDirectMovement.HandleNecessaryTargets`;
  in MP, defers mid-dash and delivers every precomputed target once at the movement endpoint, ordered by
  UniqueId, instead of sampling the caster's frame-timed live position (which could miss a target forever). Fixes
  Macabre Dance / Charge. *MP-gated; exact parity required.*
- **Preview Ghost Fix** (`PreviewGhostFix.cs`): the *uuid-mint* half of the UI-preview-ghost problem, now
  **three** patches: preview-owned `EntityFact.Attach` wrapped in `DisableStatefulRandomContext` (Prefix +
  Finalizer) so ghost facts draw ids from the non-hashed fallback; preview units excluded from
  `AreaEffectEntity.ShouldUnitBeInside` aura membership; and (v0.8.24) the whole
  `UnitHelper.Copy(..., preview: true)` holds the context in MP: vanilla's own scope closed before
  `CopyItems`, so preview *items* minted hashed uuids (capture 0.8.23 count fork). *MP-gated; exact parity
  required.*
- **Preview Rulebook Guard** (`PreviewRulebookGuard.cs`): the *rulebook-handler* half: prefix on
  `RulebookEventBus.Subscribe(IGlobalRulebookSubscriber, ISubscriptionProxy)` skips preview-owned global
  registrations (owner via `proxy.GetSubscribingEntity()`), so a ghost's handlers never fire during real combat
  and fork `RuleSystem`. *MP-gated; exact parity required (technically subset-effective: the ghost is
  client-local, but bundled under the uniform parity rule).*
- **Local Time-Scale Fix** (`LocalTimeScaleFix.cs`): the two client-local `PlayerTimeScale` writers (hashed
  `GameTime` forks): the fog-gated 16x AI-turn fast-forward is **always 1x in MP** (v0.8.15: the frustum-union
  substitution was withdrawn: `IsInCameraFrustum` culls against local `View.RenderersBounds`, a proxy, not
  deterministic; hidden AI turns run at normal speed in co-op, cosmetic pacing cost), and the local slow-mo
  pause-bind hold is neutralized by swapping only the `0.6f` constant. *MP-gated; exact parity required.*
- **Deterministic Order Fix** (`DeterministicOrderFix.cs`): `FindUnitsInRange` results sorted by UniqueId in MP
  (the engine's own `ByIdComparison`, as its sibling `FindUnitsInShape` already does), so the same hashed RNG
  draw no longer resolves to different victims per machine (Psychic Phenomena, ricochet, crossfire).
  *MP-gated; exact parity required because one-sided sorting guarantees cross-machine order disagreement.*

## Open backlog

### Open investigations

- **Weather combat exit:** `WeatherCombatExitDiag` is enabled. Compare both peers' gating predicates
  and `SetNewInclemency` calls before changing this path.
- **Tactician momentum remainder:** `TacticianDiag` is enabled. Identify the first differing
  `MomentumThisCombat` remainder, then audit the related base-only hash parts
  (`MomentumReachedTrigger`, `HunterDodge`, and `ChangeVeilDamage`).
- **Area-effect candidate census:** add scoped logging for trigger entry, final predicate, and aura
  membership. The current evidence does not support replacing the Unity trigger census globally.
- **Eogann single-entity fork:** add per-entity hash decomposition for core fields, parts, facts, and
  command state.
- **View-driven command scheduling:** audit `UnitFollowUnitController.ShouldAct`, which reads
  `View.MovementAgent.WantsToMove` before scheduling simulation commands.
- **Source-review candidates:** zone-exit mass-loot branching, mouse-hover `VirtualPosition`,
  teleport view-facing, mimic reveal, cutscene fog-pause, drop-item bag selection, and
  animation-tick gating. These need either a two-sided capture or a complete source path before a patch.

### 0.9 hardening

`../ROADMAP-0.9.md` defines the ordered hardening series: session-latched compatibility,
P2P framing, transfer ACK/NACK with per-peer fallback, sequenced-lock retry/abort, and conservative
desync inference with per-call-site LeakDetector accounting.

### Revisit when evidence changes

- Deterministic Sleep uses the deterministic verdict for combat-capable units and retains vanilla
  camera behavior for ambient units that cannot join combat. Revisit only if a capture identifies a
  transition or scene-state fork in that remaining class.
- `DataTransporter.OnMessage` routes to `m_Receivers[0]`. A transfer-routing guard remains a
  recovery-reliability option if concurrent portrait/avatar traffic is observed during join or rejoin.

### Structural limits

- LeakDetector cannot observe off-thread draws or mechanics decisions that read view state without
  calling `Rand.Get`. Those paths require source review and targeted instrumentation.

## Capture protocol

Confirmed causes were identified by diffing both peers' logs for the same desync. Record the context
(combat, cutscene, loading, or space), then archive both `GameLogFull.txt` files **before either
machine relaunches**. Diff RNG stream fingerprints at the first divergent tick. For `GlobalUuid`,
diff the entity and fact creation ring. For `sceneEntities`, diff the per-entity hash dump.

Before testing, confirm that both machines use the same MultiplayerStability version and the same UMM
mod set. The Photon mod list cannot see ToyBox or MicroPatches, so a mismatched UMM installation
invalidates the capture. The `net_desync` and `net_allow_one` cheats support local smoke testing
before a two-machine session.

## Known risks

- **Game patches**: all Harmony targets need re-verification per game update: re-grep each method/string and
  watch for the mod's own `[ERR] ... not found` log lines; the decompile can be newer than the referenced
  `Code.dll` (verify member accessibility against the live DLL).
- **Inlining**: avoid patching small private methods. `Rand.Get` is the exception; it is patched early
  at the entry point and reports when it runs. Prefer interface-dispatched seams or methods with
  `try`/`finally` bodies.
- **Mixed installs**: see the design-rule peer-compatibility categories: subset-safe (diagnostics/UI/ack-pump),
  negotiated-protocol (transfer, locks), and exact-parity-required (every sim/RNG prevention fix). Until the 0.9
  session latch ships, identical builds on every machine is the only protection for the third category.

## History (what changed from the 2026-07-02 plan)

- **AutoResync (old Phase 2.1): reversed.** The plan proposed auto-triggering `StartGameWithoutSave` on desync;
  the owner's later decision is *no auto-resync*. DesyncWatch's transition-flap logic and dialog re-show are as
  far as it goes.
- **Mod-parity gate (old Phase 0.1): deferred, then reinstated for 0.9.** Parity remains manual in
  v0.8.32 because the Photon mod list cannot see UMM mods reliably.
- Most diagnosis and prevention items in the original plan shipped under different names and were refined from
  captures. Steam P2P replaced the pure-Photon transfer boost as the main lever; the ghost fix split into two
  halves; the transition-flap suppression and Leak Detector were added after the initial plan.

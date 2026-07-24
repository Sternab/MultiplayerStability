# Patch Catalog — MultiplayerStability v0.8.32

Canonical inventory of all 23 components across 25 source files. Statuses use a strict vocabulary:
**Field validated** (post-fix two-sided capture proves the class gone) · **Mechanism confirmed;
post-fix validation pending** (defect proven from captures/source; the fix itself has not yet been
proven by a post-fix capture) · **Diagnostic only** · **Withdrawn or superseded** · **Infrastructure**.

Compatibility categories: **subset-safe** (any subset of machines) · **negotiated** (self-gates on
every-peer-modded) · **exact-parity** (simulation-changing; identical build required on every machine
until the 0.9 latch ships).

Defect-origin classes: **ENGINE** (root cause in engine code; the mod's intervention is a downstream
workaround) · **DIAG** (diagnostic tooling) · **INFRA** (mod-only infrastructure). Each entry ends
with a **root-cause note** locating the defect's origin; these are observations, not prescriptions.

---

## C01 · TransferBooster — `TransferBooster.cs` · INFRA

- **Targets:** `SaveNetManager.UploadSave` / `SaveNetManager.DownloadSave` (prefix + finalizer each).
- **Vanilla defect:** save-transfer window refills only on acks; acks are sent once per rendered frame,
  and transfers run during low-FPS loading screens → ~0.22 MB/s.
- **Mod behavior:** 8 ms `SendAcksOnly` timer pump during any transfer (always); 96 KB × 4
  chunk/window boost only when every room player runs the mod (relay force-disconnects a fast sender
  whose receiver acks slowly). Refcounted; vanilla values restored at zero.
- **Gate/category:** runs only through co-op-only seams; ack pump subset-safe, boost negotiated.
- **Failure policy:** finalizer-balanced teardown; peer check fail-open to vanilla.
- **Consequences:** none in solo; no simulation contact.
- **Status:** Field validated (measured transfer-rate change across the session record).
- **Root-cause note:** the transfer ceiling comes from the frame-clocked ack pump and the default
  chunk/window constants, not from available bandwidth.

## C02 · Steam P2P save transfer — `SteamP2P.cs` + `SteamSaveTransfer.cs` · INFRA

- **Targets:** `DataTransporter.SendSave` (prefix), `MessageNetManager.OnMessage` (prefix; claims
  Photon event code 100). Receiver completes the real `SaveNetManager.m_DownloadSaveTcs` via field
  access.
- **Vanilla defect:** all save bytes traverse the Photon relay at ~230 KB/s per peer.
- **Mod behavior:** bulk bytes over `SteamNetworkingMessages` (ICE direct / SDR relay), adaptive
  send-rate control driving `SendRateMin`; Photon remains control plane and fallback; sequential
  per-peer sends. ~8× measured speedup; 10/10 recent field transfers delivered (`fed to game=True`).
- **Gate/category:** negotiated (all peers modded + on Steam), transparent Photon fallback otherwise.
- **Failure policy:** any failure/timeout → vanilla path; anti-spoof accept-list; main-thread pump.
- **Status:** Field validated. Known 0.9 work: wire framing, ACK/NACK semantics, per-peer fallback
  (see `ROADMAP-0.9.md` items 2–3) — these are hardening gaps, not observed field failures.
- **Root-cause note:** this side channel exists only because the relay path is rate-capped; it adds
  no capability beyond bulk-byte transport.

## C03 · DesyncWatch — `DesyncWatch.cs` · DIAG

- **Targets:** desync-handler injection into `SlidingWindowDesyncDetectionStrategy`; prefix on
  `SyncStateCheckerController` sim tick; prefix on `UIDesyncHandler.RaiseDesync`; postfix on
  `SyncNetManager.HandleActorsState`; `EntityFact.Attach` postfix (uuid ring); re-arms `WasDesync`.
- **Behavior:** per-episode logs with tick + inferred `tick % 5` bucket; RNG-stream post-tick
  fingerprint ring; entity/fact-creation ring; per-entity `sceneEntities` hash dump on serious
  desyncs; suppresses the vanilla resync dialog only for confirmed randomState-only flaps during
  loading/cutscene windows, re-showing on graduation/persistence. Never auto-resyncs.
- **Category:** subset-safe (log-only + dialog policy local).
- **Known limitation:** bucket attribution is inferred (first 32-bit match in a 128-ring); field data
  shows ±40-tick `senderTick` spread between peers → 0.9 item 5 adds tick identity.
- **Status:** Infrastructure (diagnostic); the dialog-suppression policy is Field validated
  (suppresses transition flaps; correctly did **not** suppress real forks in three later captures).

## C04 · WeatherRngFix — `WeatherRngFix.cs` · ENGINE

- **Targets:** `VFXWeatherSystem.Update` (prefix + finalizer; type resolved by name —
  `Owlcat.Runtime.Visual`).
- **Vanilla defect:** render-loop weather VFX drain the hashed `Weather` stream once+ per frame;
  framerate-dependent draw counts diverge `randomState`.
- **Mod behavior:** the whole update runs under `DisableStatefulRandomContext`; deterministic sim
  consumers (`InclemencyController` in-tick) untouched. Covers nested spawn/lightning controllers.
- **Gate/category:** always-on (solo draws divert to the non-hashed fallback — VFX-only, no gameplay
  effect); exact-parity for MP effect.
- **Status:** Field validated (post-fix captures show `Weather` bit-identical through long sessions;
  281 matching diagnostic records in the latest relevant capture).
- **Root-cause note:** the engine already maintains non-serialized VFX streams (e.g. `Visuals.Fx`);
  the defect is that this render-clocked call graph reaches the serialized `Weather` stream instead.

## C05 · ProjectileRngFix — `ProjectileRngFix.cs` · ENGINE

- **Targets:** `Projectile.BeforeLaunch` (transpiler; swaps the `LinqExtensions.Random<FxBone>` call).
- **Vanilla defect:** aim-bone pick draws hashed `Projectiles` **only when** the target view has a
  `ParticlesSnapMap` — view presence is client-local → one-sided draws (burst-fire desyncs).
- **Mod behavior:** deterministic first-locator pick; the unconditional draws in the same method stay
  hashed. Zero-swap logs loudly (`PATTERN NOT FOUND`).
- **Gate/category:** always-on (low-impact solo change: fixed bone choice); exact-parity.
- **Status:** Field validated (the class disappeared from captures after the fix; later geometry hole
  addressed by C15).
- **Root-cause note:** the draw itself is cosmetic (bone choice); the desync comes from its
  *conditionality* on client-local view presence while it consumes a hashed stream.

## C06 · SequencedLocks — `SequencedLocks.cs` · INFRA

- **Targets:** `LockNetManager.Lock` / `OnLockReceived` (prefixes), baseline resets on room leave and
  save upload/download.
- **Vanilla defect:** one reused `NetLockPointId` with no sequence number → a fast client's
  next-barrier announcement is absorbed into the slow client's current barrier (stuck at 100%).
- **Mod behavior:** per-session ordinal tag on barrier announcements (code-8 payload 1→5 bytes).
- **Gate/category:** negotiated (wire-format change; inert in mixed lobbies).
- **Status:** Field validated (barriers complete in all multi-player captures since; the pre-fix hang
  shape not reproduced). 0.9 item 4 adds retry/abort.
- **Root-cause note:** the race exists because barrier announcements carry no sequence identity —
  one reused `NetLockPointId` for every barrier.

## C07 · DeterministicSleep — `DeterministicSleep.cs` · ENGINE

- **Targets:** replacing prefix on `SleepingUnitsController.Tick`; prefix+postfix pair on
  `EntityFader.Visible` setter.
- **Vanilla defect family:** the awake-unit census is camera/fog-driven (client-local) while gating
  simulation (death resolution timing, ability target counts, combat joins, AoO scans); the fog-dissolve
  fader writes the sim `AwakeTimer` from client-local visibility flips; corpses' hashed
  `IsDeathRevealed` is re-asserted per tick from client-local terms; `IsSleeping`/`IsDeathRevealed`
  setters toggle view GameObjects on change (an override-after design pays double toggles per
  disagreeing unit per tick — a measured FPS storm).
- **Mod behavior:** single-write replacing census in MP — deterministic verdict in combat and for any
  combat-capable unit in peaceful play (engine's own `CanJoinCombat` predicate; synced 40 m distance
  valve), vanilla verdict replicated verbatim for ambient units; dying units, `Sleepless`, near-party
  cutscene-held units (25 m), and starships always awake; corpses `IsDeathRevealed = true` (death is
  synced; the flag only keeps the corpse view active); fader `Wake` cancelled in MP; two-pass
  compute-then-apply; staged timer aging.
- **Gate/category:** MP-gated; exact-parity.
- **Consequences:** ambient scene-loops beyond 25 m pause until approached (visible); combat-capable
  near-party units stay awake (small perf cost, measured acceptable).
- **Status:** Field validated (census counts match exactly across peers in every capture since; the
  awake-set desync class — including the previously reproducible death-timing fork — has not recurred).
- **Root-cause note:** the defect is simulation scheduling being coupled to render culling; the mod's
  census derives the same decisions from synchronized state only.

## C08 · FogGateFix — `FogGateFix.cs` · ENGINE

- **Targets (transpilers, getter-swap):** `AreaEffectEntity.ShouldUnitBeInside(BaseUnitEntity)`,
  `UnitCombatJoinController.ShouldStartCombat`, `LOSGetter.GetBaseValue` (swaps `IsVisibleForPlayer`,
  keeping the synced `IsInGame` term), `UnitMovementAgentBase.TickMovement` (fog-gated 8× speed +
  heading snap), `PartyAwarenessController.Tick` (fog-gated awareness rolls/XP/trap triggers),
  `RicochetHelper.GetPossibleRicochetTargets` (fog-filtered candidates).
- **Vanilla defect:** mechanics decisions read the client-local fog/render flags; hashed positions,
  buff membership, awareness rolls, and combat starts diverge.
- **Mod behavior:** in MP the client-local term is dropped (units treated as not-fogged/visible for
  these mechanics only); solo exact vanilla via helper fallthrough. Per-site swap-count logs.
- **Gate/category:** MP-gated; exact-parity.
- **Status:** aura-membership + combat-start sites Field validated (the capture-5 one-entity fork
  class has not recurred); the four later sites Mechanism confirmed; post-fix validation pending.
- **Root-cause note:** the six sites are the mechanics-side reads of `IsInFogOfWar`/
  `IsVisibleForPlayer` found so far; both flags are client-local by construction.

## C09 · DashDeliveryFix — `DashDeliveryFix.cs` · ENGINE

- **Targets:** prefix on `AbilityCustomDirectMovement.HandleNecessaryTargets` (+ private
  `HandleTarget` via cached delegate).
- **Vanilla defect:** dash-through delivery polls the caster's live mid-dash **view** position per
  frame; a target can be missed forever on one client.
- **Mod behavior:** defer while really moving; deliver the full precomputed set once at the movement
  endpoint, ordered by `UniqueId`.
- **Gate/category:** MP-gated; exact-parity.
- **Consequences:** effects land at dash end rather than mid-pass (visual timing). Delivery-tick skew
  remains count-equal but is *not* guaranteed harmless (threshold-crossing accumulators can latch a
  skew — see C23).
- **Status:** Mechanism confirmed; post-fix validation pending (no post-fix capture exercised Macabre
  Dance/Charge delivery specifically).
- **Root-cause note:** the per-frame delivery poll is the one place this ability family reads a live
  view transform; the precomputed target set it ignores is already simulation state.

## C10 · PreviewGhostFix — `PreviewGhostFix.cs` · ENGINE

- **Targets:** `EntityFact.Attach` (prefix + finalizer; owner read from the `manager` parameter —
  `__instance.Owner` is null at prefix time), `AreaEffectEntity.ShouldUnitBeInside` (prefix),
  `UnitHelper.Copy(BaseUnitEntity, bool, bool, bool, bool)` (prefix + finalizer).
- **Vanilla defect:** client-local preview units (inventory dolls, level-up plans) stay subscribed to
  game events and receive combat-start buffs whose ids mint from the hashed `GlobalUuid` stream on one
  machine only; they also count as aura members (count-scaled magnitudes); and vanilla's own preview
  RNG scope closes before `CopyItems`, so preview items mint hashed uuids.
- **Mod behavior:** preview-owned fact attaches run under `DisableStatefulRandomContext`; previews are
  excluded from aura membership; the whole `Copy(..., preview: true)` holds the context in MP.
- **Gate/category:** MP-gated; exact-parity.
- **Status:** fact-attach + aura exclusion Field validated (the "+N at combat start" uuid fork class
  gone; a later capture showed large preview builds with `GlobalUuid` remaining identical); the
  `Copy` scope extension Mechanism confirmed; post-fix validation pending.
- **Root-cause note:** the root condition is preview units subscribing to gameplay events and sharing
  hashed streams with simulation; all three patched mechanisms are downstream of it.

## C11 · LeakDetector — `LeakDetector.cs` · DIAG

- **Targets:** prefix on `Rand.Get()` (the chokepoint for every hashed draw and uuid mint).
- **Behavior:** logs any draw of a serializable/hashed stream outside a deterministic simulation tick,
  with a classified call-site — names latent desyncs on **one machine with no desync required**.
  Log-only by design (auto-diverting a false positive would manufacture a desync).
- **Category:** subset-safe; works solo.
- **Proven value:** proactively caught the Solomorne dialogue draws (C16 Guard C) before the fork.
- **Known limitations:** blind to off-thread draws and to non-RNG "view flag into mechanics" leaks;
  per-stream warning budget can exhaust on startup noise (0.9 item 5 makes it per call site).
- **Status:** Infrastructure (diagnostic), Field validated as a detector.

## C12 · PreviewRulebookGuard — `PreviewRulebookGuard.cs` · ENGINE

- **Targets:** prefix on `RulebookEventBus.Subscribe(IGlobalRulebookSubscriber, ISubscriptionProxy)`.
- **Vanilla defect:** a preview unit's global rulebook handlers fire during real combat and fork the
  `RuleSystem` stream (burst-attack desync).
- **Mod behavior:** preview-owned **global** registrations are skipped at subscribe time (owner via
  `proxy.GetSubscribingEntity()`).
- **Gate/category:** MP-gated; effectively subset-effective (the ghost is client-local) but held to
  exact-parity with the rest of the simulation-changing set.
- **Status:** Field validated (post-fix capture: guard fired, `RuleSystem`/`GlobalUuid` stayed
  identical through the previously-forking scenario).
- **Root-cause note:** same root condition as C10 — a preview unit's handlers living on the global
  rulebook bus.

## C13 · LocalTimeScaleFix — `LocalTimeScaleFix.cs` · ENGINE

- **Targets (transpilers):** `TurnController.SetTime` (fog-read swap), `UnpauseController.Tick`
  (0.6f constant swap).
- **Vanilla defect:** two client-local writers of `PlayerTimeScale`, which scales the 50 ms step into
  hashed `GameTime`: a fog-gated 16× AI-turn fast-forward (fog is client-local) and a local slow-mo
  input hold. One tick of disagreement is a permanent `player`-bucket fork.
- **Mod behavior:** in MP the AI-turn scale is always 1× (a camera-frustum substitution was withdrawn:
  the frustum test culls against local renderer bounds and is a proxy, not deterministic), and the
  slow-mo constant becomes 1×.
- **Gate/category:** MP-gated; exact-parity. Cost: hidden AI turns run at normal speed in co-op.
- **Status:** Mechanism confirmed; post-fix validation pending (no `GameTime` fork has appeared since,
  but no targeted post-fix capture isolates this site).
- **Root-cause note:** one time-scale factor is already a synchronized input (`CameraFollowTimeScale`
  rides a command); these two writers are the remaining client-local ones. The withdrawn approach is
  documented in-file.

## C14 · DeterministicOrderFix — `DeterministicOrderFix.cs` · ENGINE

- **Targets:** postfix on `EntityBoundsHelper.FindUnitsInRange(Vector3, float)`.
- **Vanilla defect:** results come back in physics-broadphase order (collider creation/toggle history —
  client-local); `PsychicPhenomenaRedirect` picks a victim by index from a hashed draw → same draw,
  different victim per machine; streams stay in-hash (invisible to RNG diagnostics).
- **Mod behavior:** results sorted by `UniqueId` in MP — the engine's own `ByIdComparison`, exactly as
  the sibling `FindUnitsInShape` already does.
- **Gate/category:** MP-gated; exact-parity (one-sided sorting *guarantees* order disagreement).
- **Status:** Mechanism confirmed; post-fix validation pending.
- **Root-cause note:** the engine's own sibling `FindUnitsInShape` already orders its results with
  `ByIdComparison`; `FindUnitsInRange` is the unordered variant.

## C15 · ProjectilePositionFix — `ProjectilePositionFix.cs` · ENGINE

- **Targets:** prefix on `Projectile.GetTargetPoint`; prefix on
  `AbilityProjectileAttackLineHelper.TryGetTargetPointByRandomLocator`.
- **Vanilla defect:** projectile mechanical geometry reads live view-bone transforms: the stored
  target locator's transform feeds ricochet legs and grenade push direction (SnapMap presence is
  client-local); the starship path draws a hashed hull point only when a `StarshipView` exists; the
  attack-line helper draws hashed `UnitLogic.Abilities` gated on view presence.
- **Mod behavior:** in MP every site takes its own engine-precedented no-view fallback (entity/grid
  point + miss offset; ship position + up; grid node + 1 m) — no invented geometry.
- **Gate/category:** MP-gated; exact-parity. Visual: projectiles aim at the base point (identical to
  vanilla's SnapMap-less behavior).
- **Status:** Mechanism confirmed; post-fix validation pending.
- **Root-cause note:** comparable lockstep engines separate visual bones (rendering) from
  deterministic positions (mechanics) — observed in Solasta's engine and consistent with Dark
  Heresy's direction; every fallback this fix takes is the engine's own no-view path.

## C16 · DialogRngFix — `DialogRngFix.cs` · ENGINE

- **Targets:** property-getter wraps on `BlueprintAnswer.SkillChecks` and `SkillChecksDC`; wrap on
  `DialogController.HasNextUnselectedAnswers(BlueprintAnswer)`.
- **Vanilla defect:** dialogue UI refresh paths draw the hashed `DialogSystem` stream at view time:
  the answer preview getters (via `CharacterSelection.SelectUnit(Random)` and `CueSelection.Select`)
  and the answer-tree inspection API. Draw counts are client-local (whose UI refreshes) → mid-
  conversation `randomState` forks. Real advancement is sim-ticked and correct.
- **Mod behavior:** the three UI-inspection entry points hold `DisableStatefulRandomContext` for their
  whole body in MP (semantic caller-wraps — valid even when the UI work runs inside a tick). The real
  `SelectAnswer` pick and in-tick cue advancement keep their synced hashed draws.
- **Withdrawn:** a timing-based gate (`IsSimulationTick`) — falsified in the field; a global
  deterministic `CueSelection` replacement — it changed real narrative selection. Both documented
  in-file as do-not-reintroduce.
- **Gate/category:** MP-gated; exact-parity.
- **Status:** Field validated (post-fix three-machine capture: both guards fired on all machines, zero
  `DialogSystem` divergence through dialogue-heavy sessions; the third guard validated by a later
  capture with no dialogue leaks).
- **Root-cause note:** the defect shape is UI preview/inspection paths sharing hashed RNG streams
  with simulation; the advancement path is already correct.

## C17 · IdleAnimationRngFix — `IdleAnimationRngFix.cs` · ENGINE

- **Targets (transpiler, getter-swap with explicit signatures):**
  `UnitAnimationManager.TickIdleVariants(float)`, `UnitAnimationManager.OnAnimationSetChanged()`,
  `UnitAnimationActionMicroIdle.OnStart(UnitAnimationActionHandle)`,
  `UnitAnimationActionVariantIdle.OnStart(UnitAnimationActionHandle)`.
- **Vanilla defect:** `AnimationManager.StatefulRandom` maps idle-variety draws to the **hashed**
  `Animation3` stream on the view/animation clock; machines draw identical values ticks apart →
  transient `randomState` skew (the dominant "transition flap" noise class) that can escalate while
  draws recur.
- **Mod behavior:** the four idle sites reroute to `PFStatefulRandom.Visuals.AnimationIdle` — the
  engine's own designated non-hashed idle stream (explicitly excluded from serialization). Idle variety
  stays random per machine; the hashed stream stops moving. Dodge/special-attack picks share the
  property but fire inside synced combat execution — deliberately untouched pending their own audit.
- **Gate/category:** MP-gated; exact-parity.
- **Status:** Field validated (post-fix captures show no `Animation3` divergence; the transition-flap
  noise class stopped appearing).
- **Root-cause note:** `AnimationIdle` already exists as the engine's designated non-hashed idle
  stream; the idle call graph simply maps to hashed `Animation3` instead.

## C18 · ActionBarRoleSpamFix — `ActionBarRoleSpamFix.cs` · ENGINE (defect) / UI-only

- **Targets:** prefix on `ActionBarSlotVM.HandleRoleSet(string)`; prefixes on
  `ActionBarSlotVM.HandlePlayerEnteredRoom(Player)` / `HandlePlayerLeftRoom(Player)` (resolved by
  name + single-`Player`-parameter match).
- **Vanilla defect:** on player-leave the engine raises a role event per controlled entity (~1,500);
  `HandleRoleSet` ignores its `entityId` parameter, so all 12 slots refresh on every event and NRE on
  unitless slots — ~18,000 exception stacks in seconds into an uncapped log (600 MB field reports);
  the per-player room events repeat the shape (~425 stacks).
- **Mod behavior:** each slot refreshes only on its own unit's role events; unitless slots skip.
  **Ungated** — the filtering invariant is valid in every context including teardown (an instantaneous
  `IsMultiplayer` gate went false before 2→1 departure callbacks and disabled the guard during the
  main storm window; also raisable solo via cheats/`ForceSet`).
- **Category:** UI-only; subset-safe.
- **Status:** Field validated (post-fix capture: a real 2→1 departure produced zero exceptions).
- **Root-cause note:** `HandleRoleSet` receives an `entityId` parameter it does not use, and the log
  sink is uncapped — both directly observable in the 600 MB field logs.

## C19 · WeatherCombatExitDiag — `WeatherCombatExitDiag.cs` · DIAG

- **Targets:** prefix+postfix on `WeatherController.HandlePartyCombatStateChanged(bool)`; postfix on
  the terminal `InclemencyController.SetNewInclemency(InclemencyType, bool, float?)` (resolved by
  parameter shape; the enum lives in an unreferenced assembly).
- **Open defect (mechanism confirmed, predicate unproven):** the combat-exit inclemency choice reads
  the *visual* `VFXWeatherSystem.IsProfileOverriden` flag plus weather state; each `SetNewInclemency`
  draws hashed `Weather` and writes hashed player fields (`NextWeatherChange` is in the player hash).
  A captured episode showed one client one draw ahead at combat exit → `player` fork then
  `randomState`. Which gating input differed is not yet proven.
- **Behavior:** logs the full gating input set (both controllers' `TargetInclemency`, veil,
  profile-override, `CurrentWeatherEffect` via field reflection) + each draw with weather-vs-wind
  attribution and bracketing pre→post stream fingerprints.
- **Important boundary:** do **not** wrap this path in `DisableStatefulRandomContext` — the draws
  write hashed fields; client-random fallbacks would fork harder.
- **Status:** Diagnostic only.

## C20 · TrapPauseDiag + containment — `TrapPauseDiag.cs` · ENGINE (containment) + DIAG

- **Targets:** diagnostic prefix + finalizer on `AbstractUnitEntity.ForceRotateToDesired`; containment
  prefix (lower priority) on the same method; episode-reset prefix on
  `Game.HandleGameModeChanged(GameModeType, GameModeType)`.
- **Vanilla defect:** trap detection auto-pauses; while paused, `AbstractUnitCommand.OnRun` calls
  `ForceLookAt` **before** `DidRun = true`; `ForceRotateToDesired` writes the hashed sim orientation
  and then dereferences the client-local view/IK graph (`View.IsVisible`-gated
  `IkController.GrounderIk.ResetPosition()`); the NRE aborts `UnitCommandBuffer.Tick` mid-batch and
  the residual commands retry differently per peer → `sceneEntities` forks on the touched party
  members. Longstanding defect (present in early-campaign logs). Field counts: 72-vs-10 NREs,
  514-vs-107 residual `Cmd is already set` exceptions; three trap storms immediately preceded room
  disconnects.
- **Containment behavior (MP-only):** reimplementing prefix preserves the sim write and vanilla view
  rotation exactly; only the paused, visible-unit IK reset is null-safe (a successfully **read** null
  `IkController`/`GrounderIk` skips the reset, logged). Failure routing: metadata drift latches back
  to vanilla; a real `ResetPosition` failure surfaces once, unwrapped; reimpl failures fall back to
  vanilla (idempotent writes); orientation `FieldRef` resolves in guarded `Prepare()` (patch declines
  on rename). The engine's own sibling movement path performs the same IK null checks.
- **Diagnostic contract:** paused-window breadcrumbs budgeted per accepted-Pause episode; unique
  `(tick, unit, seq)` record keys (per-tick per-unit ordinal dictionary); all exceptions logged and
  rethrown.
- **Gate/category:** diagnostic subset-safe; containment exact-parity.
- **Status:** containment Mechanism confirmed; post-fix validation pending (no post-fix trap capture
  yet). Diagnostic Field validated as instrumentation (perfect cross-peer record match in a clean
  session).
- **Root-cause note:** the defect window is view/IK work running inside command `OnRun` between the
  sim write and `DidRun`; the engine's sibling movement path already null-checks the same IK graph.

## C21 · AugmentationBarkFix — `AugmentationBarkFix.cs` · ENGINE

- **Targets:** prefix+finalizer depth bracket on the `AugmentationsVM` constructor(s); prefix on
  `BarkBanterController.HandleBarkBanter(BlueprintBarkBanter)`.
- **Vanilla defect:** the client-local augmentation screen picks a bark with `UnityEngine.Random` and
  raises the banter event; the handler adds it to `Player.PlayedBanters` — which is in the
  synchronized player hash — one-sidedly by construction.
- **Mod behavior:** caller-scoped containment — the handler skips only while the augmentation VM
  constructor is on the stack, only in MP. Sim-side raisers (`ShowBanter` actions, system-map objects
  — verified symmetric) are untouched everywhere. Cost: no augmentation-screen bark in co-op.
- **Gate/category:** MP-gated; exact-parity.
- **Status:** Mechanism confirmed; post-fix validation pending.
- **Root-cause note:** `PlayedBanters` is in the synchronized player hash while this write originates
  from a client-local screen — a one-sided write by construction.

## C22 · ChargePathDiag + fix — `ChargePathDiag.cs` · ENGINE (fix) + DIAG

- **Targets:** prefix on `PathfindingService.FindPartialCachedPath(UnitMovementAgentBase, Vector3,
  Vector3, bool)` (the fix); resolution-source postfixes on `FindFullCachedPath`, `FindPartialCachedPath`,
  `ComputeAndCachePath`; logging postfix on `FindPathChargeTB_Blocking`.
- **Vanilla defect:** the partial charge-path cache lookup matches caster + origin + ignoreBlockers
  only — no destination key and **no target entity** — then cuts a cached path at the destination's
  node index. Aiming previews feed the same cache, so a path cached under different target occupancy
  can be cut at the enemy's occupied node and reused on the controlling client only; delivery then
  writes `Caster.Position` to that node unconditionally. Field symptom: charge → attack → parry →
  desync with both units on the same tile; `ChargeBuff` 12–13 ticks before first mismatch in both
  captured episodes. Corroboration: the Dark Heresy build of this engine removed the partial lookup
  and requires target identity on full-cache hits.
- **Mod behavior (MP-only):** partial-cache reuse disabled (prefix returns null); exact target-checked
  hits stay cached; unmatched paths recompute; solo untouched. Verification is three-signal: no
  `Partial_Patch` init error, the one-time `[ChargeFix] Active` line, and no `partial-cache` source
  lines (silence alone is inconclusive — prefix and tripwire share one patch class).
- **Gate/category:** MP-gated; exact-parity.
- **Status:** **Mechanism confirmed; post-fix validation pending** — the charge/attack/parry scenario
  has not yet been exercised on a build carrying the fix.
- **Root-cause note:** the Dark Heresy build of this engine no longer contains the partial lookup and
  keys full-cache hits on target identity — the shape this fix approximates from the outside.

## C23 · TacticianDiag — `TacticianDiag.cs` · DIAG

- **Targets:** postfix on `TacticalAdvantagePassive.OnEventDidTrigger(RulePerformMomentumChange)`.
- **Open defect (mechanism confirmed, origin unproven):** the component accumulates a fractional
  remainder in `Data.MomentumThisCombat` and adds `TacticianTacticalAdvantageBuff` on 100-crossings —
  but `Data.GetHash128` omits the accumulator, so a remainder divergence is invisible to desync
  detection until one peer crosses 100 first and mints a one-sided hashed buff (captured). What first
  split the remainders is unproven (possibly the now-fixed C22 charge skew; possibly an upstream event
  delta).
- **Behavior:** logs every momentum event with owner, delta, and post-event remainder.
- **Related audit:** the same base-only-hash omission exists in `MomentumReachedTrigger`,
  `HunterDodge`, `ChangeVeilDamage` (bounded hash audit queued; see `KNOWN-LIMITATIONS.md`).
- **Status:** Diagnostic only.
- **Root-cause note:** the accumulator is gameplay-relevant state omitted from the component hash;
  the same omission pattern exists in three sibling components (see Related audit).

---

## Withdrawn or superseded (documented for history; not in the shipped behavior)

| Item | Why withdrawn |
|---|---|
| Camera-frustum substitution for the AI-turn time scale | `IsInCameraFrustum` culls against local renderer bounds — a proxy, not deterministic |
| Global deterministic `CueSelection` replacement | Changed real narrative cue selection, not just previews |
| Timing-based (`IsSimulationTick`) dialogue guard | UI work can run inside a tick; discriminators must be semantic |
| Broad per-handler preview-rulebook reflection sweep | Replaced by the single registration-time guard (C12) |
| Corpse `IsDeathRevealed` from the frustum term | Same frustum non-determinism; replaced by always-revealed-when-dead |
| `Game.StartMode`-based pause-episode reset | Requests can reject/defer; reset moved to the accepted transition |

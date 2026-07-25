# MultiplayerStability — Plan & Status of Record

*Originally a 2026-07-02 design plan; first reconciled 2026-07-09 and **last refreshed 2026-07-24** against
manifest **v0.8.32** (source-verified against the live scripts). This is now a status-of-record + forward
backlog, not the original speculative phase list. The mod is a separate OwlcatModification + Harmony project,
nested at `Assets/Modifications/MultiplayerStability` in the Owlcat modification template and unrelated to any
other mod in that template. Author: Sternab.*

Rogue Trader co-op is deterministic lockstep (50 ms ticks, only player *commands* cross the wire via Photon).
Any client-local difference reaching the simulation forks the per-tick state hash → a desync. This mod:
**diagnoses** desyncs (make them visible + attributable), **removes root causes** where a mod safely can, and
**speeds up transfers/recovery** — while leaving the *decision* to resync with the player.

## Doctrine (the rules every component follows)

- **No auto-resync, ever** (owner decision — reverses the original plan's "AutoResync" phase). Many desyncs are
  playable-through; recovery stays the player's choice. The strongest action the mod takes is *re-showing the
  vanilla resync dialog* on a confirmed serious desync. It never calls `StartGameWithoutSave` itself.
- **Solo-safe (not always byte-identical).** Most behaviour-changing components self-gate on
  `NetworkingManager.IsMultiplayer` or run only through co-op-only seams, so solo is byte-identical there. Two
  are always-on: WeatherRngFix (weather VFX draw from the non-hashed fallback — no gameplay effect) and
  ProjectileRngFix (deterministic aim-bone pick, which feeds ricochet/push geometry — a low-impact solo change).
  Solo is unaffected *in practice*, not strictly byte-identical.
- **Best-effort fail-open.** Patching is isolated per patch class — a failing class logs `[Init][ERR]` and stays
  inert while the others continue, so a component built from several classes can be left partially active;
  runtime guards fall back to the vanilla path at their own site. A bug in this mod must never disable a real
  code path or manufacture a desync.
- **Mod parity: session-latched compatibility gate approved for 0.9** (owner decision 2026-07-16, revising the
  earlier dropped-gate call now that testers distribute builds). Design: no launch blocker — if every peer has
  the exact compatible build, simulation fixes enable; otherwise every modded peer stays on **vanilla** behavior
  and logs one clear warning. Latched per simulation epoch (initial launch / save-transfer relaunch — a joiner
  is accounted for before `PlayersReadyMask`), never reevaluated per patch call, never flipped mid-session.
  Diagnostics and UI fixes stay active regardless; deterministic simulation changes share the gate. Until 0.9
  ships, parity remains manual: match exact versions on every machine.
- **Peer-compatibility categories** (per-component tags below use these; individual asymmetry arguments were
  retired as overconfident):
  - *Subset-safe*: diagnostics (DesyncWatch, LeakDetector), UI-only fixes (ActionBarRoleSpamFix), the transfer
    ack-pump. Safe on any subset of machines.
  - *Negotiated protocol*: Steam P2P transfer, Sequenced Locks, the Photon window boost — self-gate on
    every-peer-modded and engage only when all peers participate.
  - *Exact parity required (until the 0.9 latch)*: **every** RNG- or simulation-changing prevention fix
    (Weather, Projectile RNG + Position, Deterministic Sleep incl. corpse/fader, all FogGate sites, Dash,
    both preview-ghost halves, Local Time-Scale, Deterministic Order, Dialog, Idle Animation). Mixed installs
    range from useless (Weather: no worse than vanilla, fixes nothing) to actively desync-causing
    (Deterministic Order: one-sided sorting *guarantees* order disagreement) — treat them all as requiring
    identical builds on every machine.

## Shipped components (v0.8.32)

Enter point: `MultiplayerStabilityMain.Initialize` → **per-class isolated patching** (v0.8.12: each
`[HarmonyPatch]` class patched in its own try/catch — one failing class logs `[Init][ERR]` and goes inert
instead of aborting init; a blanket `PatchAll` previously let one throw kill everything after it, including
the transfer stack) → isolated `Wire()` calls, in order: `SteamSaveTransfer`, `DesyncWatch`, `WeatherRngFix`,
`LeakDetector`, `PreviewRulebookGuard`. 23 numbered components across 25 `.cs` files (v0.8.x added
`LocalTimeScaleFix.cs`, `DeterministicOrderFix.cs`, `ProjectilePositionFix.cs`, `DialogRngFix.cs`, `IdleAnimationRngFix.cs`, `ActionBarRoleSpamFix.cs`).

### Transfer & loading (recovery-adjacent)
- **Transfer Booster** (`TransferBooster.cs`) — vanilla throttles co-op save transfer to ~0.22 MB/s (Photon's
  send window only refills on acks, and acks go once per rendered frame during low-FPS loading). Two levers via
  Prefix+**Finalizer** on `SaveNetManager.UploadSave/DownloadSave`: an 8 ms `SendAcksOnly` ack-pump (every
  transfer, one-sided safe) and a 96 KB×4 chunk/window boost (**only when every player runs the mod** — a fast
  sender without fast receiver acks gets force-disconnected by the relay). Refcounted, vanilla values restored
  at count 0. *Asymmetric: degrades gracefully (a lone install gets only the safe ack-pump).*
- **Steam P2P save transfer** (`SteamP2P.cs` + `SteamSaveTransfer.cs`) — routes the ~16 MB save payload over a
  direct Steam `SteamNetworkingMessages` channel (ICE NAT-punch + SDR fallback) instead of the ~230 KB/s Photon
  relay; **~8× measured speedup**. Photon stays the control plane; a code-100 handshake negotiates, the receiver
  completes the real `m_DownloadSaveTcs`. Mod-side AIMD rate control drives `SendRateMin`. Prefixes on
  `DataTransporter.SendSave` + `MessageNetManager.OnMessage`. *Both-Steam + both-modded, else transparent Photon
  fallback on any failure/timeout. RESOLVED — do not reopen without new field data (see SAVE-TRANSFER-SPEED.md).*
- **Sequenced Locks** (`SequencedLocks.cs`) — fixes the vanilla loading-barrier race (one reused `NetLockPointId`
  with no sequence number → a fast client's next-barrier announcement is swallowed into the slow client's current
  barrier, hanging it at 100%). Tags each barrier with a per-session ordinal. Prefix `LockNetManager.Lock`/
  `OnLockReceived`; baseline reset on room leave + save upload/download. *Self-gated on `AllPlayersModded()` (wire
  format change); solo/mixed lobby = pure vanilla.*

### Diagnosis
- **Trap/Pause Diagnostic** (`TrapPauseDiag.cs`, v0.8.20 — capture 0.8.19, two independent `sceneEntities`
  episodes) — log-only instrumentation for the OPEN trap/pause command-lifecycle class: trap detection
  auto-pauses; `OnRun`'s paused `ForceLookAt` → `ForceRotateToDesired` writes **sim** orientation then touches
  client-local View/IK (`View.IsVisible`-gated `GrounderIk.ResetPosition`); a one-machine throw there skips
  `DidRun` → command bookkeeping diverges (party members only, RNG identical; longstanding vanilla defect —
  same storm in 0.6.4 logs). Paused-window breadcrumbs (budget reset at the ACCEPTED `HandleGameModeChanged` → Pause transition, not
  the rejectable `StartMode` request; tick-regression fallback) + all exceptions logged, rethrown unchanged;
  every paused-window line carries a per-tick per-unit dictionary ordinal (interleave-proof), and the
  two-sided acceptance criterion is a unique-keyed `(tick, unit, seq)` diff — a throw on one peer against a
  successful breadcrumb at the same key on the other (both peers throwing somewhere, or bare call-count
  skew, proves nothing by itself);
  **Containment SHIPPED v0.8.26–0.8.28** (4.5h-capture evidence: 72-vs-10 NREs, 514-vs-107 residue, forks =
  command-touched units; the shared NRE aborted the command batch mid-tick): the paused visible-unit IK reset
  is null-safe in MP with a five-way failure-routing contract (best-effort logging, drift-latch to vanilla,
  `Prepare()`-gated FieldRef, single unwrapped target-exception surfacing, fail-open reimpl); the diagnostic
  stays as watchdog. Related audit item:
  `UnitFollowUnitController.ShouldAct` schedules sim commands off `View.MovementAgent.WantsToMove` (Channel B,
  can diverge with no exception). *Diagnostic half: log-only, subset-safe. Containment half (v0.8.26+):
  simulation-changing — exact parity required.*
- **Weather Combat-Exit Diagnostic** (`WeatherCombatExitDiag.cs`, v0.8.18 — capture 0.8.17-SECOND) — log-only
  instrumentation for the OPEN second weather class: at combat exit, `HandlePartyCombatStateChanged` +
  `SetNewInclemency` draw hashed `Weather` and write hashed player fields, steered by the *visual*
  `IsProfileOverriden` flag (Channel B on the sim path). One client drew once more → `player` bucket forked,
  then `randomState`, persistent. Logs the full gating input set (both controllers'
  `TargetInclemency` — the actual :347/:351 gates — veil, profile-override, `CurrentWeatherEffect` via
  field-reflection) + each `SetNewInclemency` with weather-vs-wind attribution (`m_WeatherData` reference
  identity) and bracketing pre→post fingerprints; the next two-sided capture with combat near weather names
  the differing predicate. **Not a fix**: a `DisableStatefulRandomContext` wrap would write client-random values into
  hashed fields — worse. All Owlcat.Runtime.Visual types reflective (unreferenced assembly). *Log-only;
  subset-safe.*
- **Action-Bar Role Spam Fix** (`ActionBarRoleSpamFix.cs`, v0.8.14 — tester 600 MB-log incident) — on
  player-leave the engine raises a role event per controlled entity (~1,500) and `ActionBarSlotVM.HandleRoleSet`
  ignores its `entityId` parameter, refreshing every slot on every event and NRE-ing on unitless slots:
  ~18,000 full exception stacks in seconds into an uncapped `GameLogFull` (`LogSinkFactory` passes
  `int.MaxValue`). Prefix filters each slot to its **own** unit's events and skips unitless slots — vanilla
  behavior preserved exactly for the one event that matters per slot; v0.8.16 extends the unitless skip to the
  per-player room events (`HandlePlayerEnteredRoom`/`LeftRoom`, ~155 of the 425 residual stacks in the 0.8.14
  capture); v0.8.17 removes the `IsMultiplayer` gate from both guards — instantaneous `PlayerCount > 1` goes
  false *before* 2→1 departure callbacks, so the gate disabled the guard during the storm's biggest window
  (~270 stacks); ungated because the filtering invariant is valid in every context — including teardown and
  rare non-departure raisers (`net_allow_one`, `PlayerRole.ForceSet`) — so no player-count test is required. Deliberately
  *not* a global exception suppressor (surrounding network context stays loggable). *Ungated; UI-only;
  subset-safe.*
  **Ops guidance:** zip capture logs; retain only incident sessions. A dedicated rotating MP log (~25–50 MB ×2)
  stays a longer-term option if another vanilla storm class appears.
- **DesyncWatch** (`DesyncWatch.cs`) — makes desyncs visible/attributable, never auto-resyncs. Per-episode log
  with inferred tick%5 bucket (player / sceneEntities / areaPersistent / randomState / syncData+signals);
  ring buffers for RNG-stream post-tick fingerprints, GlobalUuid entity/fact-creation names, and local tick
  hashes (two machines' logs diff to the guilty stream/entity); per-entity `sceneEntities` hash dump on serious
  desyncs; re-arms the once-per-session `WasDesync` latch on recovery. **Transition-flap policy:** a prefix on
  `UIDesyncHandler.RaiseDesync` suppresses the vanilla resync dialog for a *confirmed randomState-only* desync
  occurring in a loading/cutscene window, re-showing it only if the episode graduates to another bucket or
  persists (~10 s). Six patches + one handler injection. *Log-only; asymmetric-safe.*
- **Leak Detector** (`LeakDetector.cs`) — proactive: a prefix on `Rand.Get()` (the universal chokepoint for every
  hashed RNG draw + uuid mint) logs any draw of a serializable/hashed stream that fires *outside* a deterministic
  sim tick — naming the leaking call site on **one machine, no desync required** (works solo). Log-only (no
  firewall — the fallback is non-deterministic, so auto-diverting a false positive would manufacture a desync).
  *Blind spots (inherent): off-thread/Pathfinding draws, and Channel-B "view flag read by mechanics" leaks that
  never pass through `Rand.Get`.*

### Prevention (root-cause fixes; most require both machines)
- **Charge-Path Fix + Diagnostic** (`ChargePathDiag.cs`, fix v0.8.30 — tester same-tile report + decompile
  mechanism + Dark Heresy corroboration; no capture needed) — RT's partial charge-path cache lookup matches
  caster+origin+ignoreBlockers only (no destination key, **no target**) and cuts a cached — possibly
  aiming-preview-polluted — path at the destination node; delivery writes `Caster.Position` to that node
  unconditionally → the charge/parry same-tile desyncs. In MP partial reuse is disabled (exact target-checked
  hits kept; unmatched recompute) — Dark Heresy's newer shape, which removed the lookup entirely. The
  resolution diagnostic stays as tripwire; verification = no `Partial_Patch` init error + `[ChargeFix] Active`
  + no `partial-cache` lines (silence alone is inconclusive). *MP-gated; exact parity required.*
- **Tactician Diagnostic** (`TacticianDiag.cs`, v0.8.29, log-only) — every momentum event logged with owner,
  delta, and post-event `MomentumThisCombat` remainder: the accumulator is **omitted from its part hash**, so
  remainder divergence is invisible until one peer crosses 100 and mints a one-sided buff. Origin of the
  split unproven (possibly the now-fixed charge path); same base-only-hash mistake in `MomentumReachedTrigger`,
  `HunterDodge`, `ChangeVeilDamage` — bounded hash audit queued. *Log-only; subset-safe.*
- **Augmentation Bark Fix** (`AugmentationBarkFix.cs`, v0.8.24 — capture 0.8.23 `player`-bucket fork) — the
  client-local augmentation screen picked a bark via `UnityEngine.Random` and its handler wrote hashed
  `Player.PlayedBanters` one-sidedly. Caller-scoped flag around the `AugmentationsVM` ctor; `HandleBarkBanter`
  skips while set in MP — sim-side raisers (etude `ShowBanter`, system-map objects; all verified symmetric)
  untouched. Cost: no augmentation-screen bark in co-op. *MP-gated; exact parity required.*
- **Weather RNG Fix** (`WeatherRngFix.cs`) — wraps `VFXWeatherSystem.Update` in `DisableStatefulRandomContext`
  (Prefix + **Finalizer**) so render-frame weather VFX stop draining the hashed `Weather` stream. The wrap covers
  the whole `Update → UpdateWeather → UpdateAllControllers` chain, so the per-controller spawn/lightning draws
  (`WeatherMinMaxRateSpawnController.Update`, `WeatherLightningBoltController.Spawn`) run nested inside it and are
  already covered — no separate patch needed. *Not MP-gated (benign in solo — the wrapped draws are pure-VFX).*
- **Projectile RNG Fix** (`ProjectileRngFix.cs`) — transpiler on `Projectile.BeforeLaunch` swaps the
  view-conditional `Random<FxBone>` draw (fires only when the target View has a `ParticlesSnapMap` = client-local)
  for a deterministic first-locator pick. *Both-required.*
- **Projectile Position Fix** (`ProjectilePositionFix.cs`, v0.8.7 — the Solasta-doctrine catch) — the geometry
  half of the projectile family: in MP, `Projectile.GetTargetPoint` and the attack-line random-locator take the
  engine's own deterministic **no-view fallbacks** (entity/grid point + miss offset; ship position + up; grid
  node + 1m) instead of live view-bone transforms. Closes the ricochet/push geometry hole the RNG fix left
  (SnapMap-present vs SnapMap-absent clients computed different mechanics with identical RNG), retires the
  long-open `GetTargetPointForStarship` sibling and its conditional hashed hull-point draw, and removes the
  view-gated `UnitLogic.Abilities` stream draw in `TryGetTargetPointByRandomLocator`. Visual cost in MP:
  projectiles aim at the target's base point — identical to vanilla's own SnapMap-less behavior. *MP-gated;
  both-required.*
- **Dialog RNG Fix** (`DialogRngFix.cs`, v0.8.10 — two-sided capture 0.8.8, Codex-identified drawer) — the
  dialogue UI's answer-preview getters (`BlueprintAnswer.SkillChecks` / `SkillChecksDC`) run at view time and
  draw the hashed `DialogSystem` stream (`SkillChecksDC` → `CharacterSelection.SelectUnit(Random)`; `SkillChecks`
  → `CueSelection.Select(Random)`), so preview frequency — client-local by nature — forks `randomState`
  mid-conversation. Two timing-independent guards: **(A)** both preview getters hold
  `DisableStatefulRandomContext` for their whole body in MP (semantic: preview-by-definition, valid even when
  executing inside a sim tick); the real `SelectAnswer` acting-unit pick keeps its synced hashed draw.
  **Guard C (v0.8.20):** the third convicted view-time caller — `DialogController.HasNextUnselectedAnswers`
  (answer-tree inspection, sole external caller `AnswerVM`; the Solomorne fork, caught proactively by the
  LeakDetector) — gets the same semantic whole-body wrap.
  (A former Guard B — deterministic first-eligible cue — was **removed in v0.8.15**: `CueSelection.Select`
  serves real narrative progression, so it changed actual story selection in co-op; the semantic preview wrap
  alone covers the capture-convicted path.) *MP-gated; both-required.* (Failed predecessors, falsified by
  capture 0.8.8: the `IsSimulationTick` timing gate — preview can run in-tick — and the out-of-scope
  `CharacterSelection` drawer.)
- **Idle Animation RNG Fix** (`IdleAnimationRngFix.cs`, v0.8.11 — three-machine capture 0.8.10, Codex-identified)
  — the **Animation3 flapper class at its source**: `AnimationManager.StatefulRandom` maps idle-variety draws
  (micro/variant idle triggers, speed jitter, retrigger trackers) to the *hashed* `Animation3` stream on the
  view/animation clock, so idle timing skews `randomState` transiently (the classic flapper behind most
  transition-window noise) and can escalate to serious when draws recur while skewed. The engine's own
  `PFStatefulRandom.Visuals.AnimationIdle` is the designated **non-hashed** idle stream (explicitly excluded
  from serialization) — the idle call graph just used the wrong property. In MP, a transpiler swaps the
  `get_StatefulRandom` read in the four idle sites (`TickIdleVariants`, `OnAnimationSetChanged`,
  `MicroIdle.OnStart`, `VariantIdle.OnStart`) for `AnimationIdle`; idle variety stays random per-machine (pure
  view), the hashed stream stops moving. Dodge/special-attack variant picks share the property but fire inside
  synced combat execution — deliberately untouched pending their own audit. *MP-gated; both-required.*
- **Deterministic Sleep** (`DeterministicSleep.cs`) — replacing prefix on `SleepingUnitsController.Tick` (one change-guarded write per unit per tick -- the postfix design double-wrote disagreeing units and their setters toggle view GameObjects; the Thassera FPS regression) rebuilds the awake
  census, killing the whole awake-set desync class (death timing, ability counts, turn/AoO triggers, combat-join).
  **Census policy (v0.8.x):** deterministic verdict in combat AND, in peaceful play, for every *combat-capable*
  unit (the engine's own `CanJoinCombat` predicate — combat starts are decided on the previous tick's census, so
  those verdicts must be deterministic before the fight exists); ambient units that can never join combat keep the
  vanilla camera verdict (bridge-perf carve-out). Always-awake invariants: dying units, `Sleepless` units
  (vanilla parity), cutscene-held units near the party (synced distance), starships. Also writes corpses'
  hashed `IsDeathRevealed` = **true** for finally-dead units (v0.8.15: death is synced; the earlier frustum term
  was withdrawn — `IsInCameraFrustum` tests local renderer bounds, a proxy, not deterministic; watch item:
  corpse-heavy maps keep more views active — FPS check), and cancels the fog-dissolve fader's sim-side
  `Wake` in MP (`EntityFader` patch in the same file). Two-pass compute-then-apply so *compute-phase* failures leave the
  vanilla census untouched (a failure in the trivial apply pass could still leave partial state — never
  observed; the apply path is list ops and property sets). *MP-gated; exact parity required.*
- **Fog Gate Fix** (`FogGateFix.cs`) — transpiler dropping a client-local visibility term from the **six**
  mechanics decisions that consume one: `AreaEffectEntity.ShouldUnitBeInside` (aura membership),
  `UnitCombatJoinController.ShouldStartCombat` (NPC combat start), `LOSGetter.GetBaseValue` (swaps
  `IsVisibleForPlayer`→visible, keeping the synced `IsInGame` term; `HasLOS` stays the real gate),
  `UnitMovementAgentBase.TickMovement` (fog-gated 8× speed + heading snap — hashed positions),
  `PartyAwarenessController.Tick` (fog-gated awareness rolls/XP/trap triggers), and
  `RicochetHelper.GetPossibleRicochetTargets` (fog-filtered ricochet candidates). Internal types resolved by
  name. (The fog-gated 16× AI-turn time-scale uses a different policy — see Local Time-Scale Fix.) *Both-required.*
- **Dash Delivery Fix** (`DashDeliveryFix.cs`) — prefix on `AbilityCustomDirectMovement.HandleNecessaryTargets`;
  in MP, defers mid-dash and delivers every precomputed target once at the movement endpoint, ordered by
  UniqueId, instead of sampling the caster's frame-timed live position (which could miss a target forever). Fixes
  Macabre Dance / Charge. *MP-gated; both-required.*
- **Preview Ghost Fix** (`PreviewGhostFix.cs`) — the *uuid-mint* half of the UI-preview-ghost problem, now
  **three** patches: preview-owned `EntityFact.Attach` wrapped in `DisableStatefulRandomContext` (Prefix +
  Finalizer) so ghost facts draw ids from the non-hashed fallback; preview units excluded from
  `AreaEffectEntity.ShouldUnitBeInside` aura membership; and (v0.8.24) the whole
  `UnitHelper.Copy(..., preview: true)` holds the context in MP — vanilla's own scope closed before
  `CopyItems`, so preview *items* minted hashed uuids (capture 0.8.23 count fork). *MP-gated; exact parity
  required.*
- **Preview Rulebook Guard** (`PreviewRulebookGuard.cs`) — the *rulebook-handler* half: prefix on
  `RulebookEventBus.Subscribe(IGlobalRulebookSubscriber, ISubscriptionProxy)` skips preview-owned global
  registrations (owner via `proxy.GetSubscribingEntity()`), so a ghost's handlers never fire during real combat
  and fork `RuleSystem`. *MP-gated; exact parity required (technically subset-effective — the ghost is
  client-local — but bundled under the uniform parity rule).*
- **Local Time-Scale Fix** (`LocalTimeScaleFix.cs`) — the two client-local `PlayerTimeScale` writers (hashed
  `GameTime` forks): the fog-gated 16× AI-turn fast-forward is **always 1× in MP** (v0.8.15 — the frustum-union
  substitution was withdrawn: `IsInCameraFrustum` culls against local `View.RenderersBounds`, a proxy, not
  deterministic; hidden AI turns run at normal speed in co-op, cosmetic pacing cost), and the local slow-mo
  pause-bind hold is neutralized by swapping only the `0.6f` constant. *MP-gated; both-required.*
- **Deterministic Order Fix** (`DeterministicOrderFix.cs`) — `FindUnitsInRange` results sorted by UniqueId in MP
  (the engine's own `ByIdComparison`, as its sibling `FindUnitsInShape` already does), so the same hashed RNG
  draw no longer resolves to different victims per machine (Psychic Phenomena, ricochet, crossfire).
  *MP-gated; exact parity required — one-sided sorting GUARANTEES cross-machine order disagreement (mixed is worse than vanilla).*

## Open backlog

**Actionable root-cause fixes (unbuilt):**
- `Projectile.GetTargetPointForStarship` — space-combat sibling of the projectile fix (needs a two-sided
  space-combat capture to confirm it fires before patching).
- **Channel-B audit outcome (2026-07-09, shipped v0.8.1):** 18 confirmed hazards; nine shipped in v0.8.1
  (v0.8.0 was superseded pre-deploy by a Codex review: `Sleepless` census invariant + two-pass apply)
  (fog-gated 16× AI-turn time-scale → frustum-union substitution; fog 8× movement; local slow-mo; combat-capable
  census rule + corpse reveal-flag + EntityFader wake-cancel; awareness rolls; ricochet fog filter;
  FindUnitsInRange ById sort). **Capture-gated remainder:** teleport view-facing (16 abilities incl. AI
  Deathmarks), mimic reveal, cutscene fog-pause read (inverted polarity — verify vs cutscene-hold first),
  DropItem bag pick, animation-tick gate. **Verify-first:** mouse-hover `AbilityTargetEmptyCell` VirtualPosition
  pair (Family F never got its adversarial pass) and the `CombatAnimSpeedUp` synced-or-not contradiction. Zone-exit
  ("mass loot") `sceneEntities` fork still needs its clean two-sided capture.

**Intentional design — revisit on evidence (not bugs):**
- Deterministic Sleep is full-deterministic *only in combat* (perf: full determinism everywhere tanked FPS on
  NPC-dense maps). Revisit if a capture shows a cutscene→combat or scene-entity fork the mode-switch misses.
- **Transfer routing guard** (originally Phase 0.3): `DataTransporter.OnMessage` routes to `m_Receivers[0]`
  unconditionally, so a concurrent avatar/portrait transfer during join/rejoin can abort the save download.
  Recovery-reliability, not desync prevention (~80 LOC, client-safe). Worth it before any resync-UX work.

**Inherent limits (a mod cannot fully close):**
- Leak Detector's off-thread and Channel-B blind spots. Channel-B (a view flag read by a mechanics decision)
  stays a manual grep-and-audit discipline — the `Rand.Get` chokepoint can't see it.

## Diagnosis / capture protocol

Every root cause so far was named by **diffing a two-sided capture** (the same desync from both machines). When
one fires: note the context (combat/cutscene/loading/space), then — **before either machine relaunches** —
archive both `GameLogFull.txt` into `Mod Build Logs <version>[ suffix]\`. Diff the RNG-stream fingerprints to the
first diverging tick+stream; if `GlobalUuid`, diff the entity/fact-creation ring; if `sceneEntities`, diff the
per-entity hash dump. Preflight: both machines on the same MultiplayerStability version, and (manually) the same
UMM mod set — the Photon mod-list can't see ToyBox/MicroPatches, so a mismatched UMM install spoils the capture.
Much is solo-testable via the `net_desync` / `net_allow_one` cheats before a two-machine session.

## Standing risks

- **Game patches**: all Harmony targets need re-verification per game update — re-grep each method/string and
  watch for the mod's own `[ERR] ... not found` log lines; the decompile can be newer than the referenced
  `Code.dll` (verify member accessibility against the live DLL).
- **Inlining**: never patch small private methods (`Rand.Get` is the exception, patched early at the enter point
  and self-verified by its own firing logs) — prefer interface-dispatched seams or try/finally-bodied methods.
- **Asymmetric installs**: see the Doctrine peer-compatibility categories — subset-safe (diagnostics/UI/ack-pump),
  negotiated-protocol (transfer, locks), and exact-parity-required (every sim/RNG prevention fix). Until the 0.9
  session latch ships, identical builds on every machine is the only protection for the third category.

## History (what changed from the 2026-07-02 plan)

- **AutoResync (old Phase 2.1) — reversed.** The plan proposed auto-triggering `StartGameWithoutSave` on desync;
  the owner's later decision is *no auto-resync*. DesyncWatch's transition-flap logic and dialog re-show are as
  far as it goes.
- **Mod-parity gate (old Phase 0.1) — dropped.** Enforced manually instead (and the Photon mod list can't see UMM
  mods anyway).
- The original plan's diagnosis/prevention ideas mostly shipped, under different names and with field-driven
  refinements (Steam P2P replaced the pure-Photon transfer boost as the main lever; the ghost fix split into two
  halves; the transition-flap suppression and Leak Detector were added after the initial plan).

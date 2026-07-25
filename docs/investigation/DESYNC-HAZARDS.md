# Verified Desync Hazards

*From a 76-agent investigation (2026-07-02): 111 raw hazard candidates → 99 unique → top 30 adversarially
verified (each by an independent verifier, then a second skeptic trying to refute it). Result: **17 confirmed**,
10 plausible-but-refuted, 3 rejected, 69 lower-ranked left unverified. Full evidence with complete verdict
reasoning: the raw investigation record and findings digest are retained privately by the author and
are not published.*

Severity below = verifier's final assessment. All cites are decompiled-source paths (assembly-relative).

## Class A — Systemic amplifiers (make any fault permanent)

1. **`GameMode.Tick` swallows controller exceptions per-client** — HIGH.
   Every controller tick is wrapped in `try/catch { log; continue }`
   (`RogueTrader.GameCore\Kingmaker\GameModes\GameMode.cs:52-59`). A client-local exception (null view,
   missing asset, mod fault) aborts the rest of that controller's work on ONE client; lockstep keeps running
   diverged. Converts any transient one-sided exception into a permanent silent desync.
2. **Peer stall freezes the simulation indefinitely** — MEDIUM (UX: severe).
   `RealTimeController.cs:272-280` just skips ticks; `BackgroundPing` keeps the Photon connection alive from a
   timer thread even when the peer's main thread is hung — so Photon never times them out and a manual kick is
   the only unblock. No banner, no timeout, no "waiting for player X".
3. *(Context, verified under Class A but reported separately)* **No recovery path**: detection sub-2 s is
   silent (empty potential-handler list), "serious" fires once per session, and the resulting dialog is a
   dead end — the sim keeps running diverged (see ARCHITECTURE.md).

## Class B — Client-local state reaching the deterministic simulation

The dominant *root-cause* class. The simulation reads camera/fog/view/input state that legitimately differs
per client:

4. **Camera/FOW-derived sleep state gates which units tick at all** — HIGH.
   `SleepingUnitsController.ShouldBeSleeping` uses `IsInCameraFrustum` / `IsInFogOfWar`
   (`SleepingUnitsController.cs:88-104`); only awake units are ticked by every `BaseUnitController`. The
   frustum union uses all players' *synced* cameras, but per-entity frustum membership depends on
   **`View.RenderersBounds`** — client-local renderer state (`EntitiesInCameraFrustumController.cs:92`). Which
   units simulate ≠ identical across clients.
5. **Hashed `PartLifeState.m_IsDeathRevealed` written from camera frustum + view visibility** — MEDIUM-HIGH.
   `SleepingUnitsController.cs:59` writes it from `IsInCameraFrustum && IsVisibleForPlayer`
   (`View.IsVisible` = view-layer); the field is **in the state hash** (`PartLifeState.cs:285`).
6. **`Entity.m_IsRevealed` (hashed, `Entity.cs:1054`) latched from view visibility** — HIGH (as hash-content
   hazard; the pure fog path was refuted as deterministic, but the `EntityViewBase.SetVisible` latch path
   (`EntityViewBase.cs:343-355`) and death-reveal remain).
7. **Local Unpause keybind scales simulation time** — HIGH.
   `UnpauseController.Tick` writes `PlayerTimeScale = InvertPauseButtonPressed ? 0.6f : 1f` every sim tick from
   raw local input (`UnpauseController.cs:23`); GameTime (hashed, persisted) advances at different rates while
   one player holds the key.
8. **TB AI-turn 16× fast-forward keyed to fog-of-war** — MEDIUM.
   `TurnController.SetTime` sets `PlayerTimeScale=16` when the AI unit `IsInFogOfWar`
   (`TurnController.cs:821-834`); fog activity itself depends on scene view objects
   (`FogOfWarScheduleController.cs:89,118-124`). Same-agreeing-fog is the common case (why this is only
   MEDIUM), but the input is not tick-deterministic.
9. **Mouse-hover `VirtualPosition` consulted during synced ability execution** — MEDIUM.
   `UnitPredictionManager` writes it from local hover/Ctrl-preview (`UnitPredictionManager.cs:401,418`);
   `AbilityTargetEmptyCell.IsTargetRestrictionPassed` reads it inside deterministic target checking
   (`AbilityTargetEmptyCell.cs:31`).
10. **Fog gates RNG-consuming awareness rolls, ricochet targets, AoE membership, combat join** — MEDIUM.
    `PartyAwarenessController` (Simulation tick) skips fogged objects and otherwise rolls `RulePerformSkillCheck`
    — consuming the hashed RuleSystem stream and writing saved state + XP (`PartyAwarenessController.cs:48-115`).
11. **Zone-exit loot fork** — HIGH. `AreaTransitionGroupCommand.OnAction` branches on
    `MassLootHelper.CanLootZone()` (reveal-state-derived): one client can open the loot screen while the other
    proceeds to the actual area transition (`AreaTransitionGroupCommand.cs:83-99`).

## Class C — RNG discipline breaks (hashed streams drained one-sided)

12. **Weather VFX drains the hashed `Weather` stream once per rendered frame** — HIGH.
    `WeatherMinMaxRateSpawnController.Update` rolls `PFStatefulRandom.Weather.value` on render-frame cadence
    (`WeatherMinMaxRateSpawnController.cs:24,38`; driver `VFXWeatherSystem.cs:190`). Two clients at different
    frame rates diverge the RandomState hash in any thunder/lightning area. *Directly matches the community
    "desyncs got worse in certain areas" pattern; the same stream is also consumed by real mechanics
    (`InclemencyController.SampleWeather`).*
13. **Dice/roll APIs advance hashed RNG when called from client-local code** — HIGH.
    `RulebookEvent.Dice.D` / D10/D100 (`RulebookEvent.cs:220-272`) consume the hashed RuleSystem stream with no
    guard; anything triggering a rule/roll/loot/entity-creation (GlobalUuid, `EntityFact.cs:877`) on one client
    only shifts hashed streams. This is the engine-level mechanism behind Owlcat's own patch notes: *"desync
    from hovering the cursor over Apexalium Stimulant in combat"*, *"opening the inventory in combat"* — UI
    paths that forgot the `DisableStatefulRandomContext` guard. **Also the #1 rule for OUR mod code.**
14. **Projectile RNG draws conditioned on client-local view state** — HIGH.
    `Projectile.BeforeLaunch` draws Speed (gates *which tick* hit rules fire) from a serializable stream, but
    the same stream is also drawn **only when `unitEntity.View.ParticlesSnapMap` exists** — a view-presence
    branch (`Projectile.cs:408,440-450`). View exists on one client, missing on the other → stream offsets
    diverge → subsequent combat rolls differ. *Strong match for the community's "shotguns / burst-fire /
    heavy bolters desync" cluster.*

## Class D — Mod ecosystem (highest leverage for us)

15. **Mod parity is never enforced** — HIGH (two independent finders confirmed).
    `ModsNetManager.IsSameMods` is dead code (zero callers); join/launch gate only on Ironman + DLC; the lobby
    mod warning is broken (`NetLobbyVM.cs:896-944`, GroupBy-on-array-reference bug). Mismatched sim-affecting
    mods silently desync. **Any stability mod must enforce its own parity (see the mod-parity doctrine in
    `MOD-PLAN.md`, and `../ROADMAP-0.9.md` item 1).**
16. **Synced cheat commands execute one-sided when cheat databases differ** — MEDIUM.
    `RunCheatCommandGameCommand` broadcasts the command string; a client without that `[Cheat]` registered
    swallows `CommandNotFoundException` and executes nothing (`RunCheatCommandGameCommand.cs:51-99`).
17. **Simulation stall/freeze interactions with mods** — covered by #1/#2: a mod that throws on one client
    (or registers commands asymmetrically) is *amplified* into a permanent desync by the engine.

## Notable near-misses (plausible but refuted — read before assuming)

- **Unsynced-command escape hatches** (`IsSynchronized=false`, `RunImmediate`): mechanism fully real, but no
  *vanilla* code path misuses it — it's purely the mod-facing footgun (that's why MOD-PLAN has a transport rule).
- **Trade-window start command unsynced outside capital**: real mechanism (`FactionVendorInformationVM.cs:37`),
  refuted only because the triggering UI is gated in ways that make one-sided execution unlikely in vanilla.
- **`FindUnitsInRange` raw Physics2D overlap order**: refuted in the 2026-07-02 pass on the assumption that an
  identical physics history implies an identical broadphase order; the later audit found the order also depends
  on collider creation/toggle history, which is client-local. **Superseded — see C14 in the mod's
  `../PATCH-CATALOG.md`.**
- **Dismemberment / async-void cheats / fog-reveal-only paths**: structurally real, divergence path refuted.
- **`ResurrectionLogic` view-forward placement** (`ResurrectionLogic.cs:67`, sibling of the teleport look-at
  leak): mechanism fully real — a render-frame coroutine writes `caster.Position + View.ViewTransform.forward
  * 2f` into hashed `m_Position` (`AbstractUnitEntity.cs:694`), and the no-LOS *else* branch samples the
  `View.CenterTorso` bone (line 51), so both branches leak view state and the setter never re-snaps — but the
  component is **dead content**: its only carrier is `ResurrectionBuff` (`12f2f2cf326dfd743b2cce5b14e99b3c`),
  referenced solely by `SystemMechanicsRoot.m_ResurrectionBuff`, whose property has zero callers in the full
  decompile and whose GUID appears in no other blueprint; every live resurrect path goes through
  `PartLifeState.Resurrect`, which never attaches it (verified 2026-07-09). Re-check `ResurrectionBuff` callers
  after game updates; if it ever wires in, the fix is `caster.OrientationDirection` (`AbstractUnitEntity.cs:358`,
  the engine's own sim-side forward) in place of the view forward, MP-gated.
- 69 lower-ranked candidates were never verified — the list is in the unpublished findings digest
  (notable: order-sensitive dictionary hashing in `ComponentsDataHasher`; initiative sorting with non-total
  comparer; `DataTransporter` routes all data packets to `m_Receivers[0]` — this one is *used* in MOD-PLAN;
  `SettingsNetManager` whitelist gaps; space-combat exit double-submit race).

## Correlation with community reports

Web sweep (sources in the unpublished findings digest, §WEB): desyncs cluster on
**Cassia AoE-kills / Molten Beam targeting** (→ Class B death-reveal/awake-set + Class C targeting-preview RNG),
**UI-in-combat hover/inventory actions** (→ Class C #13; Owlcat has patched several of exactly these),
**simultaneous player actions** (→ lockstep edge cases, partially fixed in 1.2.0.25), **space combat** (never
mapped — see gaps), **late-act saves** (more entities + more visited areas = bigger hash surface + slower
transfers), and **loading/saving mid-combat**. Owlcat ships desync fixes in nearly every patch; there is **no
community stability mod** (mods are usually desync *causes* — Toybox notoriously). The Pathfinder WotR
community co-op mod solved the same problems with host-authoritative sync; this project instead removes root
causes and leaves recovery to the player (see the no-auto-resync doctrine in `MOD-PLAN.md`).

## Open questions (completeness critic)

Highest-value unexplored areas, verbatim list in the digest: animation-driven rule timing
(`ActEventsCounter` vs `PretendActDelay` when a View is missing), the projectile/starship subsystem
(view transforms feeding mechanics — `Projectile.cs:349-370`), PhysX cross-machine determinism, pathfinding
worker-thread task continuations, AI decision pipeline, **space combat** (a top community desync cluster,
never audited), cutscene/etude gating, the remaining ~135 unaudited GameCommand types, hash *coverage* gaps
(state living outside the six hashed roots can diverge undetected), and culture/locale string handling.

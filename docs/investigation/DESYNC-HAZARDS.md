# Desync Hazard Review

An initial source audit on 2026-07-02 produced 111 candidates, reduced to 99 unique items. The top
30 received a second verification pass. The result was **17 confirmed**, 10 plausible but refuted,
3 rejected, and 69 lower-ranked items left unverified. The raw audit record and full findings digest
are retained privately.

Severity is the final assessment from that review. All citations use assembly-relative paths in the
decompiled source.

## Class A: Systemic amplifiers

1. **`GameMode.Tick` swallows controller exceptions per-client**: HIGH.
   Every controller tick is wrapped in `try/catch { log; continue }`
   (`RogueTrader.GameCore\Kingmaker\GameModes\GameMode.cs:52-59`). A client-local exception (null view,
   missing asset, mod fault) aborts the rest of that controller's work on ONE client; lockstep keeps running
   diverged. Converts any transient one-sided exception into a permanent silent desync.
2. **Peer stall freezes the simulation indefinitely**: MEDIUM (UX: severe).
   `RealTimeController.cs:272-280` skips ticks; `BackgroundPing` keeps the Photon connection alive from a
   timer thread even when the peer's main thread is hung, so Photon never times them out and a manual kick is
   the only unblock. No banner, no timeout, no "waiting for player X".
3. *(Context, verified under Class A but reported separately)* **No recovery path**: detection sub-2 s is
   silent (empty potential-handler list), "serious" fires once per session, and the resulting dialog does
   not restore synchronized state. The simulation continues after divergence (see ARCHITECTURE.md).

## Class B: Client-local state reaching the deterministic simulation

The simulation reads camera, fog, view, and input state that can legitimately differ per client:

4. **Camera/FOW-derived sleep state gates which units tick at all**: HIGH.
   `SleepingUnitsController.ShouldBeSleeping` uses `IsInCameraFrustum` / `IsInFogOfWar`
   (`SleepingUnitsController.cs:88-104`); only awake units are ticked by every `BaseUnitController`. The
   frustum union uses all players' *synced* cameras, but per-entity frustum membership depends on
   **`View.RenderersBounds`**: client-local renderer state (`EntitiesInCameraFrustumController.cs:92`). Which
   units simulate != identical across clients.
5. **Hashed `PartLifeState.m_IsDeathRevealed` written from camera frustum + view visibility**: MEDIUM-HIGH.
   `SleepingUnitsController.cs:59` writes it from `IsInCameraFrustum && IsVisibleForPlayer`
   (`View.IsVisible` = view-layer); the field is **in the state hash** (`PartLifeState.cs:285`).
6. **`Entity.m_IsRevealed` (hashed, `Entity.cs:1054`) latched from view visibility**: HIGH (as hash-content
   hazard; the pure fog path was refuted as deterministic, but the `EntityViewBase.SetVisible` latch path
   (`EntityViewBase.cs:343-355`) and death-reveal remain).
7. **Local Unpause keybind scales simulation time**: HIGH.
   `UnpauseController.Tick` writes `PlayerTimeScale = InvertPauseButtonPressed ? 0.6f: 1f` every sim tick from
   raw local input (`UnpauseController.cs:23`); GameTime (hashed, persisted) advances at different rates while
   one player holds the key.
8. **TB AI-turn 16x fast-forward keyed to fog-of-war**: MEDIUM.
   `TurnController.SetTime` sets `PlayerTimeScale=16` when the AI unit `IsInFogOfWar`
   (`TurnController.cs:821-834`); fog activity itself depends on scene view objects
   (`FogOfWarScheduleController.cs:89,118-124`). Fog normally agrees across peers, but this input is not
   tick-deterministic.
9. **Mouse-hover `VirtualPosition` consulted during synced ability execution**: MEDIUM.
   `UnitPredictionManager` writes it from local hover/Ctrl-preview (`UnitPredictionManager.cs:401,418`);
   `AbilityTargetEmptyCell.IsTargetRestrictionPassed` reads it inside deterministic target checking
   (`AbilityTargetEmptyCell.cs:31`).
10. **Fog gates RNG-consuming awareness rolls, ricochet targets, AoE membership, combat join**: MEDIUM.
    `PartyAwarenessController` (Simulation tick) skips fogged objects and otherwise rolls `RulePerformSkillCheck`
    which consumes the hashed RuleSystem stream and writes saved state + XP
    (`PartyAwarenessController.cs:48-115`).
11. **Zone-exit loot fork**: HIGH. `AreaTransitionGroupCommand.OnAction` branches on
    `MassLootHelper.CanLootZone()` (reveal-state-derived): one client can open the loot screen while the other
    proceeds to the actual area transition (`AreaTransitionGroupCommand.cs:83-99`).

## Class C: Hashed RNG streams advanced asymmetrically

12. **Weather VFX drains the hashed `Weather` stream once per rendered frame**: HIGH.
    `WeatherMinMaxRateSpawnController.Update` rolls `PFStatefulRandom.Weather.value` on render-frame cadence
    (`WeatherMinMaxRateSpawnController.cs:24,38`; driver `VFXWeatherSystem.cs:190`). Two clients at different
    frame rates diverge the RandomState hash in any thunder/lightning area. *Directly matches the community
    "desyncs got worse in certain areas" pattern; the same stream is also consumed by real mechanics
    (`InclemencyController.SampleWeather`).*
13. **Dice/roll APIs advance hashed RNG when called from client-local code**: HIGH.
    `RulebookEvent.Dice.D` / D10/D100 (`RulebookEvent.cs:220-272`) consume the hashed RuleSystem stream with no
    guard; anything triggering a rule/roll/loot/entity-creation (GlobalUuid, `EntityFact.cs:877`) on one client
    only shifts hashed streams. This mechanism is consistent with Owlcat patch notes such as *"desync
    from hovering the cursor over Apexalium Stimulant in combat"*, *"opening the inventory in combat"*: UI
    paths that did not use the `DisableStatefulRandomContext` guard. The same constraint applies to mod code.
14. **Projectile RNG draws conditioned on client-local view state**: HIGH.
    `Projectile.BeforeLaunch` draws Speed (gates *which tick* hit rules fire) from a serializable stream, but
    the same stream is also drawn **only when `unitEntity.View.ParticlesSnapMap` exists**: a view-presence
    branch (`Projectile.cs:408,440-450`). View exists on one client, missing on the other -> stream offsets
    diverge -> subsequent combat rolls differ. This is consistent with community reports involving
    shotguns, burst fire, and heavy bolters.

## Class D: Mod compatibility

15. **Mod parity is not enforced**: HIGH.
    `ModsNetManager.IsSameMods` is dead code (zero callers); join/launch gate only on Ironman + DLC; the lobby
    mod warning does not reliably compare content (`NetLobbyVM.cs:896-944`, GroupBy-on-array-reference
    behavior). Mismatched simulation-affecting
    mods silently desync. This project therefore requires manual parity until the compatibility latch
    ships (see `MOD-PLAN.md` and `../ROADMAP-0.9.md` item 1).
16. **Synced cheat commands execute one-sided when cheat databases differ**: MEDIUM.
    `RunCheatCommandGameCommand` broadcasts the command string; a client without that `[Cheat]` registered
    swallows `CommandNotFoundException` and executes nothing (`RunCheatCommandGameCommand.cs:51-99`).
17. **Simulation stall/freeze interactions with mods**: covered by #1/#2. A mod that throws on one client
    or registers commands asymmetrically can produce a permanent desync.

## Rejected or superseded hypotheses

- **Unsynced-command escape hatches** (`IsSynchronized=false`, `RunImmediate`): these APIs exist, but no
  *vanilla* code path misuses them. This remains a mod-facing risk and is covered by the transport
  rules in `MOD-PLAN.md`.
- **Trade-window start command unsynced outside capital**: the path exists
  (`FactionVendorInformationVM.cs:37`), but the triggering UI gates make one-sided execution unlikely.
- **`FindUnitsInRange` raw Physics2D overlap order**: refuted in the 2026-07-02 pass on the assumption that an
  identical physics history implies an identical broadphase order; the later audit found the order also depends
  on collider creation/toggle history, which is client-local. **Superseded: see C14 in the mod's
  `../PATCH-CATALOG.md`.**
- **Dismemberment / async-void cheats / fog-reveal-only paths**: the source patterns exist, but the
  proposed divergence paths were not supported.
- **`ResurrectionLogic` view-forward placement** (`ResurrectionLogic.cs:67`, sibling of the teleport look-at
  leak): a render-frame coroutine writes `caster.Position + View.ViewTransform.forward
  * 2f` into hashed `m_Position` (`AbstractUnitEntity.cs:694`), and the no-LOS *else* branch samples the
  `View.CenterTorso` bone (line 51), so both branches leak view state and the setter never re-snaps. The
  component is **dead content**: its only carrier is `ResurrectionBuff` (`12f2f2cf326dfd743b2cce5b14e99b3c`),
  referenced solely by `SystemMechanicsRoot.m_ResurrectionBuff`, whose property has zero callers in the full
  decompile and whose GUID appears in no other blueprint; every live resurrect path goes through
  `PartLifeState.Resurrect`, which never attaches it (verified 2026-07-09). Re-check `ResurrectionBuff` callers
  after game updates; if it ever wires in, the fix is `caster.OrientationDirection` (`AbstractUnitEntity.cs:358`,
  the engine's own sim-side forward) in place of the view forward, MP-gated.
- 69 lower-ranked candidates were never verified. The unpublished findings digest includes
  order-sensitive dictionary hashing in `ComponentsDataHasher`; initiative sorting with non-total
  comparer; `DataTransporter` routes all data packets to `m_Receivers[0]`, which is tracked in `MOD-PLAN.md`;
  `SettingsNetManager` whitelist gaps; and a space-combat exit double-submit race.

## Correlation with community reports

Community reports collected in the unpublished findings digest mention desyncs around
**Cassia AoE-kills / Molten Beam targeting** (-> Class B death-reveal/awake-set + Class C targeting-preview RNG),
**UI-in-combat hover/inventory actions** (-> Class C #13; Owlcat has patched several of exactly these),
**simultaneous player actions** (-> lockstep edge cases, partially fixed in 1.2.0.25), **space combat** (never
mapped: see gaps), **late-act saves** (more entities + more visited areas = bigger hash surface + slower
transfers), and **loading/saving mid-combat**. These reports are correlation data, not proof of a
specific mechanism. This project patches identified deterministic-input paths and leaves recovery
under player control (see `MOD-PLAN.md`).

## Open questions

Unreviewed areas listed in the private findings digest include animation-driven rule timing
(`ActEventsCounter` vs `PretendActDelay` when a View is missing), the projectile/starship subsystem
(view transforms feeding mechanics: `Projectile.cs:349-370`), PhysX cross-machine determinism, pathfinding
worker-thread task continuations, AI decision pipeline, **space combat** (a top community desync cluster,
never audited), cutscene/etude gating, the remaining ~135 unaudited GameCommand types, hash *coverage* gaps
(state living outside the six hashed roots can diverge undetected), and culture/locale string handling.

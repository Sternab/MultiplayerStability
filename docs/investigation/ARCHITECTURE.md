# Rogue Trader Co-op Architecture

Source analysis of the multiplayer implementation, compiled on 2026-07-02 from eight subsystem
reviews. The private subsystem reports contain approximately 120 file and line references.

## The model: deterministic lockstep over Photon

Only **player commands** cross the wire. Each client runs the full simulation independently and must
produce bit-identical state. Clients compare state hashes to detect divergence.

- **Transport:** one Photon Realtime `LoadBalancingClient` over UDP; *everything* is `OpRaiseEvent(...,
  SendReliable)` on one channel (`PhotonManager.cs:831-840, 1383`). Event codes: 1=LoadSave, 2=RequestSave,
  7=Commands, 8=Lock, 9=Kick, 21-32=bulk data, 40/41=portraits (`MessageNetManager.cs`). All traffic relays
  through Photon Cloud: no P2P.
- **Tick:** fixed **50 ms** simulation step == network step (`RealTimeController.cs:16-21`). `Runner.Update`
  drives `Game.Tick` per render frame; up to 9 catch-up ticks per frame, wall-time accumulation capped at
  250 ms (`Game.cs:931-948`).
- **Three channels ride one per-tick `UnitCommandMessage`** (`UnitCommandMessage.cs:21-33`):
  **GameCommands** (UI/meta/inventory/dialog/chargen actions), **UnitCommandParams** (move/ability orders:
  the movement path is computed locally and serialized *inside* the command as `ForcedPath`), and
  **SynchronizedData** (camera, gamepad stick, reported lag, **state hash**).
- **Gate:** tick N+1 will not execute until *every* player in `PlayersReadyMask` has delivered its bucket for
  that tick (even an empty one): `RealTimeController.cs:190-223`, `CommandQueue.2.cs:95-99`. A stalled peer
  freezes everyone's simulation **indefinitely** (rendering continues); there is no timeout, auto-pause, or
  auto-kick. 18-slot ring buffer; packets outside `[tick-8, tick+18]` are dropped with only a log
  (`CommandNetManager.cs:82-88`).
- **Lag compensation:** commands sent at tick T execute at `T + speedMode` where speedMode (0..8) converges
  deterministically on all clients from exchanged `maxLag` values (`TimeSpeedController.cs:65-122`). Local
  players get no early execution: everyone's commands go through the same queue.
- **Ordering:** within a tick, players' buckets execute in ascending `NetPlayer.Index` (= position in the
  sorted `actorNumbersAtStart` list frozen at save upload).
- **Unsynchronized paths:** `GameCommand.IsSynchronized` defaults **false**. Unsynced
  commands execute locally-only (`GameCommandQueue.cs:199-218`); unsynced unit commands bypass the buffer via
  `RunImmediate` (`PartUnitCommands.cs:166-186`). Safe only when the simulation itself generates them
  identically on every client. **Any mod/UI code that mutates state from client-local input through these
  paths silently desyncs.**
- Pause is a synced game mode (packets keep flowing while paused). Turn-based combat is not a special tick
  mode: `TurnController` runs through the normal Simulation-tick controller path.

## RNG determinism

- ~50 named xorshift128 streams in `PFStatefulRandom` (`Rand.cs:50-61`). Mechanics **must** use the
  serializable streams (RuleSystem, Mechanics, UnitRandom, Blueprints/loot, GlobalUuid ...); visual/UI streams
  (UI, Visual, Fx, Particles, NonDeterministic, Camera ...) are client-local and unhashed
  (`PFStatefulRandom.cs:89-108`).
- Dice: `RulebookEvent.Dice.D` -> `PFStatefulRandom.RuleSystem.Range` (`RulebookEvent.cs:268`).
- **Every entity/fact UniqueId comes from the hashed GlobalUuid stream** (`Uuid.cs:54-64`, `EntityFact.cs:877`).
  Creating an entity on only one client corrupts both RNG synchronization and entity identifiers.
- Seeding: co-op start = host picks a 32-bit noise (`UnityEngine.Random`), ships it in `SaveMetaData.randomNoise`;
  **all** clients call `PFStatefulRandom.OverrideRandomNoise(noise)` then load the same transferred save:
  streams are re-seeded `f(index+noise)` / `f(saved.x+noise)` (`PFStatefulRandom.cs:139-164`).
- UI/preview code avoids draining hashed streams via `ContextData<DisableStatefulRandomContext>` (under it,
  `Rand.Get` falls through to `UnityEngine.Random` without advancing stream state: `Rand.cs:52-55`).

**Implementation constraints:** mechanics randomness only from serializable streams inside synced execution;
cosmetics only from non-serializable streams; wrap previews in `DisableStatefulRandomContext`; never
`Guid.NewGuid`/`System.Random` into game state; never spawn entities client-locally; ship identical mod
builds to both players.

## Desync detection and recovery

- Every sim tick, `SyncStateCheckerController` hashes state via source-generated `IHashable.GetHash128`
  walkers. **Hash roots** (`HashableState.cs:20-31`): Player, SceneEntitiesState, AreaPersistentState,
  RandomState, SynchronizedData collection *(stub: contributes nothing:
  `PlayerCommandsCollection.cs:89-92`)*, SignalService.
- **Rotation:** only 1 of 5 roots is hashed per tick (`tick % 5`, `HashCalculator.cs:59-75`): each subsystem
  is verified at 4 Hz; detection can lag the divergent tick by up to ~41+ ticks.
- The 32-bit truncated hash rides in `SynchronizedData.stateHash`; next tick each client compares all
  players' hashes (`SyncStateCheckerController.cs:56-91`).
- **Sliding-window strategy** (`SlidingWindowDesyncDetectionStrategy`): first mismatch fires the "potential
  desync" handler list, which is empty in release. Desyncs shorter than two seconds are therefore silent. Only
  mismatches persisting **>41 ticks (~2 s)** become "serious": a message box (`UIDesyncHandler`: Yes opens
  the lobby, No keeps playing diverged) plus a full-state JSON upload to a hardcoded Owlcat endpoint
  (`http://89.17.52.236:5060`, `SendToRemoteDesyncHandler.cs:53`). Fires **once per lobby session**
  (`WasDesync` latch). A local-dump handler (`SaveToFolderDesyncHandler`) exists in the assembly but is
  never registered.
- **There is no automatic recovery.** The simulation never stops or resyncs. The only resync path is manual:
  host re-launches from the in-game lobby -> `NetGame.StartGameWithoutSave()` snapshots the host's live game,
  uploads it, everyone reloads (FSM explicitly permits Playing->UploadSave; `NetGame.cs:82-84, 236-239`;
  `NetLobbyVM.cs:405` even whitelists it when `Sync.HasDesync`). **This machinery is what an auto-resync mod would
  have to drive. This project does not trigger it automatically; see the recovery policy in `MOD-PLAN.md`.**

## Session lifecycle and save transfer

- FSM: Platform init -> Photon connect -> Create/Join lobby (9-char room code, max 6 players) -> host uploads
  save -> everyone loads -> Playing (`NetGameFsm\NetGame.cs:43-86`).
- Save transfer: host repacks the save zip (strips screenshots, recompresses fog), broadcasts meta
  (randomNoise, settings, DLCs, actorNumbersAtStart), then the zip in **48 KB chunks / 3 ack'd streams /
  33 ms-per-chunk pacing**: fully dissected with the fix plan in [SAVE-TRANSFER-SPEED.md](SAVE-TRANSFER-SPEED.md).
- Mid-session join exists but is **passive**: the room stays open, a joiner idles "Waiting for LoadSave msg"
  until the *next* save transfer (e.g. host presses Launch again): `JoiningLobbyState.cs:102`. Roles remap
  by UserId on load (`PlayerRole.PostLoad`), so a rejoining player recovers their companions.
- Disconnect: any real disconnect -> `StopPlaying` (leave room, back to lobby/menu); **no `ReconnectAndRejoin`
  anywhere**. If one player of two drops, `StopPlayingIfLastPlayer` kills the whole session
  (`PhotonManager.cs:644-658`). Sync barrier: exactly one lock point (`NetLockPointId.LoadingProcess`), used
  only at area-load ends; dialogs, vendors, character generation, and space-combat exit have no barrier and rely on
  synced commands.

## Mods, settings, cheats

- Each client publishes its mod list (Id+Version, from `UserModsData.UsedMods`) as Photon player property
  `"m"` (`ModsNetManager.cs:50-59`). **The parity check `IsSameMods` is dead code: zero callers.** Nothing
  blocks mismatched mods; the lobby warning is broken (fires for *any* mods). Doorstop-injected UMM mods are
  invisible entirely. A simulation-affecting mod on one client can therefore cause a silent desync.
  **Parity must be enforced by the mod itself** (see the compatibility rules in `MOD-PLAN.md` and
  `../ROADMAP-0.9.md` item 1).
- OwlcatModification-template mods are listed in `UsedMods`: identity = manifest UniqueName+Version.
- **39 settings sync** (all difficulty, autopause, TB pacing, gore) via a byte-index protocol
  (`SettingsNetManager.cs:15-66`); host's values win at join and **permanently overwrite the joiner's local
  settings**; mid-game changes are last-writer-wins. Mod-added settings are not synced unless appended at
  the end because the array index is part of the wire format.
- Console cheats are lockstep-synced `RunCheatCommandGameCommand`s (`CheatGameCommandSystem.cs:47-66`): a
  usable co-op transport for mod actions. Caveat: a cheat registered on only one client executes
  one-sided (receiver swallows CommandNotFound) -> desync; and cheat bodies are `async void` (keep them
  synchronous).
- Custom synced command *types* need identical MemoryPack union registration on both clients
  (`CodeDynamicUnionFormatters.cs:16-18`): mismatch is a permanent stall.

## Built-in QA and test tools

- `net_desync_default` cheat -> per-tick desync detection (fires on the first mismatching tick): useful while
  testing our own mods' determinism (`SyncNetManager.cs:85-89`).
- `replay_log_on` + `StateSerializationController` -> per-tick JSON state dumps, byte-diffable across clients
  (bit-exact float encoding via `FloatAsIntConverter`).
- `net_packet <kb>` -> runtime chunk-size change for save transfer (`CheatNetManager.cs:274`).
- `net_state` -> dump full HashableState JSON. QA `NetworkingOverlay` shows sync status and skipped ticks.
- `net_allow_one` (toggles `CheatState.AllowRunWithOnePlayer`): lets one machine run co-op flows
  without a second peer.

// Deterministic awake census for multiplayer -- the structural fix for the whole awake-set desync class.
//
// Vanilla SleepingUnitsController decides per tick which units are simulated, using camera frustum and
// fog-of-war (SleepingUnitsController.cs:88-104) -- client-local inputs, so each co-op machine simulates a
// DIFFERENT subset of the same world. Everything that consumes the awake set then diverges: death
// resolution timing (UnitLifeController -> the poison-backfire turn fork), multi-target ability counts,
// turn-start triggers, attack-of-opportunity scans, combat-join membership -- each minting facts/entities
// (GlobalUuid draws) on one client only. Field-verified twice via fingerprint diffs (2026-07-02).
//
// Instead of exempting consumers one by one, this REBUILDS the census after the vanilla pass, from
// synchronized state only, preserving the vanilla optimization's intent:
//   sleep: not-in-game, fake, suppressed-out-of-combat (all synced flags) -- vanilla's deterministic rules;
//   sleep: idle "extra" trash mobs FAR from every party member (>40m, synced positions) -- replaces the
//          camera/fog test with a distance test, keeping big-map performance without per-client inputs;
//   awake: everything else -- in particular anything in combat, anything at 0 HP still resolving death,
//          STARSHIPS (space combat is a handful of units at a scale where the distance valve is nonsense),
//          and anything under an ACTIVE cutscene that is NEAR the party (synced distance; sleep state
//          gates cutscene pause/resume, so it must resolve identically everywhere or scripted
//          spawns/actions skew across clients -- far ambient loops pause deterministically instead,
//          keeping cutscene-dense maps like the bridge performant).
// The rebuild iterates State.AllUnits in entity-pool order (identical on all clients), so the awake LIST
// ORDER -- which drives controller processing order and hence RNG consumption order -- is also identical.
// SetNewAwakeUnits clears and refills, making the override complete. Vanilla AwakeTimer nudges are ignored
// on purpose: some Wake() callers may be view-driven, and their effect would reintroduce nondeterminism.
//
// Seam: REPLACING PREFIX on SleepingUnitsController.Tick (public instance, IControllerTick dispatch --
// inlining-proof; skip-original in MP so each unit gets exactly ONE IsSleeping write per tick -- the
// earlier postfix double-wrote disagreeing units and the setter's View.UpdateViewActive() per change was
// the Thassera FPS regression). Vanilla's ambient verdict is replicated verbatim (VanillaShouldSleep).
// Multiplayer only; solo keeps the vanilla pass untouched (prefix returns true). Asymmetric-install safe:
// vanilla pairs already diverge here; both-modded eliminates the class.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker;
using Kingmaker.AreaLogic.Cutscenes;
using Kingmaker.Controllers;
using Kingmaker.Controllers.Combat;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Mechanics.Entities;
using Kingmaker.Networking;
using Kingmaker.View;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(SleepingUnitsController), nameof(SleepingUnitsController.Tick))]
    internal static class SleepingUnitsController_Tick_DeterministicSleep_Patch
    {
        private const float FarDistanceSq = 40f * 40f;             // combat-capable / bystander sleep valve
                                                                    // (generous buffer over join-scan vision)
        // Separate, tighter radius for the ambient cutscene-hold (v0.8.6, measured: Thassera co-op held 21
        // scene-loops running at 40m -- James's solo-vs-co-op A/B pinned the residual chug on exactly this;
        // vanilla pauses those loops off-camera, so holding a plaza's worth is real extra simulation).
        // Any synced radius is deterministically correct -- this only trades ambient liveliness vs perf.
        private const float CutsceneHoldDistanceSq = 25f * 25f;

        private static readonly List<AbstractUnitEntity> s_awake = new List<AbstractUnitEntity>();
        private static readonly List<AbstractUnitEntity> s_pendingUnits = new List<AbstractUnitEntity>();
        private static readonly List<bool> s_pendingSleep = new List<bool>();
        private static bool s_loggedActive;
        private static bool s_loggedError;
        private static int s_censusTicks;

        // PREFIX with skip-original in multiplayer (v0.8.3) -- NOT a postfix. The postfix design ran AFTER
        // vanilla's pass, so every unit where the two verdicts disagreed got TWO IsSleeping writes per tick
        // (vanilla's, then ours) -- and the IsSleeping/IsDeathRevealed setters call View.UpdateViewActive()
        // on every change. In Thassera that was ~32 disagreeing units x 2 view toggles x 20 ticks/s = a
        // constant GameObject-activation storm (the "dreadful FPS" regression; the awake COUNT was never the
        // cost -- census showed awake 96 vs vanilla 128). As a replacing prefix, each unit gets exactly ONE
        // write of a stable value; the setters' change-guards then no-op in steady state. Vanilla's pass is
        // replicated verbatim below (VanillaShouldSleep) for the ambient branch, so solo AND fail-open paths
        // are byte-faithful: any exception -> return true -> the original untouched vanilla Tick runs.
        private static bool Prefix()
        {
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return true;                                 // solo: vanilla Tick runs, we do nothing
                // Census policy: full deterministic verdict while the party fights (synced flag, bounded
                // counts) AND -- since the Channel-B audit -- for any unit that COULD join combat, even in
                // peaceful play (combat starts are decided on the previous tick's census, so combat-capable
                // units' sleep must be deterministic BEFORE the fight exists). Ambient units that can never
                // join combat keep the vanilla camera verdict, preserving the voidship-bridge perf carve-out.
                // Overrides that always hold: dying units resolve death on the same tick everywhere;
                // cutscene-held-near-party and starships never sleep; corpses' reveal flag re-asserted
                // deterministically below.
                bool combatMode = Game.Instance.Player.IsInCombat;
                int vanillaAwake = Game.Instance.State.AllAwakeUnits.Count;
                int heldCount = 0;
                s_awake.Clear();
                s_pendingUnits.Clear();
                s_pendingSleep.Clear();
                foreach (var unit in Game.Instance.State.AllUnits)   // entity-pool order: same on all clients
                {
                    bool dying = unit.HitPointsLeft <= 0 && !unit.LifeState.IsFinallyDead;
                    // Second always-invariant (field-proven by the Pascal-spawn fork, capture 10): a unit
                    // under an ACTIVE cutscene must resolve its sleep state IDENTICALLY on all clients,
                    // because sleep gates cutscene pause/resume (CutscenePlayerData.TickScene ->
                    // AllAnchorsInactive) and downstream commands (unit SPAWNS) fork if one client's
                    // cutscene runs while the other's is paused. Deterministic does NOT mean always-awake:
                    // holding by synced party DISTANCE pauses far ambient loops on the same tick everywhere
                    // (safe) while keeping nearby scene actors running. The v0.6.5 unconditional hold kept
                    // EVERY ambient-loop actor awake and tanked FPS on cutscene-dense maps (voidship bridge
                    // crew are all loop-cutscene anchors -- the second bridge perf regression).
                    bool cutsceneHeld = InActiveCutscene(unit) && NearParty(unit, CutsceneHoldDistanceSq);
                    if (cutsceneHeld)
                        heldCount++;
                    // Starships are NEVER slept. Space combat runs with combatMode=True, but the distance
                    // valve is meaningless at ship scale (ships sit hundreds of units apart, all read as
                    // ">40m = far") and idle allied ships aren't flagged IsInCombat -- so the valve slept
                    // them, and a slept ship's view doesn't render until a move command wakes it (James's
                    // "allied ships only appear after moving", census showed awake 2 vs vanilla 4). Space
                    // combat is a handful of units, so always-awake is free.
                    bool held = dying || cutsceneHeld || (unit is StarshipEntity);
                    // Channel-B audit rank 3: combat STARTS are always decided on the PREVIOUS tick's census
                    // (UnitCombatJoinController is registered before SleepingUnitsController), and the join
                    // scan + its enemy.IsAwake term read that census directly. So any unit that CAN join
                    // combat must take the deterministic distance verdict even in peaceful mode -- otherwise
                    // a fog-slept-on-one-machine unit joins combat (minting facts/initiative/position snap)
                    // on the other machine only: the capture-5-shaped encounter-boundary fork. Ambient units
                    // failing CanJoinCombat (Extra/Passive/etc.) keep the vanilla verdict (bridge perf).
                    bool sleep = combatMode || IsCombatCapable(unit)
                        ? ShouldSleepDeterministic(unit, held)
                        : (VanillaShouldSleep(unit) && !held);       // replicated vanilla verdict + the overrides
                                                                     // (we REPLACE vanilla's pass, so we must
                                                                     // compute its verdict ourselves -- reading
                                                                     // unit.IsSleeping here would be last tick's)
                    // Two-pass: COMPUTE everything first, mutate nothing yet -- so if any verdict throws,
                    // the outer catch leaves the vanilla census genuinely untouched (the fail-open promise;
                    // the old single-pass version had already half-mutated the census by that point).
                    s_pendingUnits.Add(unit);
                    s_pendingSleep.Add(sleep);
                }
                // Apply pass: trivial assignments only. Per-unit write ORDER matches vanilla (corpse reveal
                // FIRST, then IsSleeping -- SleepingUnitsController :57-:69): both setters call
                // UpdateViewActive() and view activeness depends on both flags, so the reversed order could
                // flicker a corpse's view off/on exactly when its state changes (Codex catch).
                for (int i = 0; i < s_pendingUnits.Count; i++)
                {
                    var unit = s_pendingUnits[i];
                    // Channel-B audit rank 6 (corrected v0.8.15): vanilla writes, for every finally-dead
                    // unit each tick, IsDeathRevealed = IsInCameraFrustum && IsVisibleForPlayer -- BOTH
                    // terms are client-local, and IsDeathRevealed is HASHED. The v0.8.1 fix substituted
                    // IsInCameraFrustum alone, believing it deterministic (union of synced cameras) -- but
                    // the frustum test culls against View.RenderersBounds (EntitiesInCameraFrustumController
                    // :92), which is LOCAL renderer state (pose/LOD/view presence), so camera-edge corpses
                    // could still diverge the hash (external review catch). The only genuinely synced policy:
                    // a corpse's death IS synced, so reveal it, period. The flag exists to keep a seen
                    // corpse's view active; always-revealed just means off-screen corpse views stay active
                    // too (fog still culls them visually) -- identical on every machine by construction.
                    if (unit.LifeState.IsFinallyDead)
                        unit.LifeState.IsDeathRevealed = true;
                    bool sleep = s_pendingSleep[i];
                    unit.IsSleeping = sleep;
                    if (!sleep)
                        s_awake.Add(unit);
                }
                Game.Instance.State.SetNewAwakeUnits(s_awake);
                // Periodic census line (~60s): quantifies what the census costs vs vanilla on this map --
                // the data that decides perf questions (bridge FPS) instead of guessing. Deliberately BEFORE
                // the timer aging so the aging loop is literally the last work in the pass (Codex round 9:
                // with logging after it, a logger throw could rerun vanilla and double-age timers).
                if (++s_censusTicks >= 1200 || !s_loggedActive)
                {
                    s_censusTicks = 0;
                    s_loggedActive = true;
                    MultiplayerStabilityMain.Log("[DetSleep] census: awake " + s_awake.Count
                        + " (prev-tick census " + vanillaAwake + "), cutsceneHeld " + heldCount
                        + ", combatMode=" + combatMode + ".");
                }
                // Timer aging, the FINAL work in the pass (plain float ops -- nothing after this executes
                // except the return): vanilla ages AwakeTimer inside ShouldBeSleeping, which every unit
                // passed through -- our deterministic branch bypasses that method, so without this loop a
                // Wake()'d combat-capable unit would keep a positive timer forever. Policy: any timer >= 0
                // ages by the synced sim DeltaTime, for EVERY unit, uniformly. (Deliberate, documented
                // divergence from vanilla, which freezes timers for units slept by its earlier clauses --
                // suppressed/camera-frozen; uniform aging is deterministic and cannot strand a timer.)
                // Everything that can throw precedes this loop and stages no timer mutations, so a failure
                // can never double-age a timer when the fail-open path lets vanilla rerun.
                float dt = Game.Instance.TimeController.DeltaTime;
                for (int i = 0; i < s_pendingUnits.Count; i++)
                {
                    var unit = s_pendingUnits[i];
                    if (unit.AwakeTimer >= 0f)
                        unit.AwakeTimer -= dt;
                }
                return false;                                    // census applied -- skip vanilla's pass
            }
            catch (Exception e)
            {
                if (!s_loggedError)
                {
                    s_loggedError = true;
                    // Compute-phase failures leave ALL unit state untouched -- verdicts AND timers; the
                    // compute pass is pure since v0.8.4 (VanillaShouldSleep no longer mutates AwakeTimer;
                    // aging is staged last in apply) -- and vanilla's own Tick then runs normally (return
                    // true). An apply-pass failure could leave partial flag writes before vanilla re-runs
                    // -- never observed; the apply path is list ops and change-guarded property sets, and
                    // timer aging sits after every throwing operation, so timers can never double-age.
                    MultiplayerStabilityMain.Log("[DetSleep][ERR] census rebuild failed, vanilla pass runs instead: " + e);
                }
                return true;                                     // fail-open: vanilla Tick executes
            }
        }

        // PURE replica of vanilla SleepingUnitsController.ShouldBeSleeping (decompile :88-104) for the
        // ambient branch, since the prefix REPLACES vanilla's pass. Vanilla mutates AwakeTimer inside this
        // method; here the verdict only READS it (same pre-decrement semantics as vanilla's >= 0 check) and
        // the aging is STAGED into the apply pass (Codex catch: (a) the deterministic branch bypassed the
        // decrement entirely, so a Wake()'d combat-capable unit kept a positive timer forever; (b) a
        // compute-phase mutation broke the "untouched vanilla on exception" claim and risked a double
        // decrement when vanilla reran after a failure). Deliberately client-local (camera/fog) exactly
        // like vanilla: this branch only serves units that can never join combat, the accepted ambient class.
        private static bool VanillaShouldSleep(AbstractUnitEntity unit)
        {
#pragma warning disable 612
            if (!unit.IsInGame || unit.Blueprint.IsFake || (unit.Suppressed && !unit.IsInCombat))
#pragma warning restore 612
                return true;
            if (!unit.Sleepless && unit.FreezeOutsideCamera
                && CutsceneControlledUnit.IsFreezingAllowed(unit) && !unit.IsInCameraFrustum)
                return true;
            if (unit.AwakeTimer >= 0f)
                return false;                    // read-only: aging happens in the apply pass
            return !unit.Sleepless && CutsceneControlledUnit.IsSleepingAllowed(unit)
                && ((unit.IsExtra && !unit.IsDead && (unit.IsInFogOfWar || !unit.IsInCameraFrustum))
                    || (unit.IsInFogOfWar && !unit.IsInCombat && unit.Commands.Empty));
        }

        private static bool ShouldSleepDeterministic(AbstractUnitEntity unit, bool held)
        {
            // Blueprint.IsFake is [Obsolete] but still the only "trap actor / non-mechanical placeholder"
            // flag, and vanilla SleepingUnitsController.ShouldBeSleeping reads it too -- match it for parity.
#pragma warning disable 612
            if (!unit.IsInGame || unit.Blueprint.IsFake)
#pragma warning restore 612
                return true;
            if (held || unit.IsInCombat)
                return false;
            if (unit.Suppressed)
                return true;
            // Vanilla invariant (ShouldBeSleeping :94/:103): a non-suppressed Sleepless unit NEVER sleeps
            // via the camera/fog clauses -- roaming spawner units and follower behaviours rely on it. This
            // check was missing since v0.6.1 (combat mode) and the combat-capable peaceful rule widened the
            // exposure (Codex catch). Order matches vanilla: Suppressed sleep wins over Sleepless.
            if (unit.Sleepless)
                return false;
            // Everything else sleeps by synced DISTANCE alone (replaces the camera/fog test for ALL unit
            // kinds). Deliberately NO Commands.Empty gate (the Thassera FPS regression, v0.8.1): city crowds
            // walk routes, so their command queue is never empty -- with the gate they could NEVER sleep and
            // whole narrative maps stayed awake. Vanilla's own off-camera freeze pauses mid-walk NPCs all the
            // time, so distance-pausing a busy unit is precedented; both inputs (positions, commands) are
            // synced, so the verdict stays deterministic. Behavior note: far-away units walking toward the
            // party (>40m scripted approaches) doze until within range -- deterministic on both machines.
            // NOTE: deliberately NOT vanilla's CutsceneControlledUnit.IsSleepingAllowed here either -- it
            // reads the cutscene's Paused flag, which is itself set from client-local sleep state (capture
            // 10); active-cutscene units are already held awake above, deterministically.
            return !NearParty(unit, FarDistanceSq);
        }

        // "Under an active cutscene" via the entry itself (GetCurrentlyActive), NOT vanilla's
        // IsSleepingAllowed -- that predicate answers "is the cutscene paused", and the pause flag is
        // written from client-local sleep state, so it diverges across clients.
        private static bool InActiveCutscene(AbstractUnitEntity unit)
        {
            var controlled = unit.CutsceneControlledUnit;
            return controlled != null && controlled.GetCurrentlyActive() != null;
        }

        // Synced-distance valve for the cutscene hold: party positions are lockstep state, so this verdict
        // is identical on every client -- unlike the camera test it replaces.
        private static bool NearParty(AbstractUnitEntity unit, float sqDistance)
        {
            var party = Game.Instance.Player.PartyAndPets;
            for (int i = 0; i < party.Count; i++)
            {
                if ((unit.Position - party[i].Position).sqrMagnitude <= sqDistance)
                    return true;
            }
            return false;
        }

        // "Could this unit enter combat?" -- the engine's own join predicate (public static), so the set of
        // units whose sleep verdict must be deterministic is exactly the set the join scan can act on, and
        // it self-maintains if the engine's rules change. All its inputs are synced state.
        private static bool IsCombatCapable(AbstractUnitEntity unit)
        {
            var baseUnit = unit as BaseUnitEntity;
            return baseUnit != null && UnitCombatJoinController.CanJoinCombat(baseUnit);
        }
    }

    // Channel-B audit rank 4: EntityFader.set_Visible (the fog-dissolve FADE effect -- pure view code) calls
    // EntityData.Wake(~2s) on every client-local visibility flip, writing the sim-side AwakeTimer that the
    // vanilla peaceful sleep verdict honors. Every fog boundary crossing therefore minted a one-sided awake
    // window at exactly the reveal moments where combat starts -- the amplifier that manufactured census
    // divergence even when both clients' fog eventually agreed. In multiplayer, cancel THIS caller's Wake by
    // restoring the pre-call AwakeTimer (never touching Wake() itself -- sim-legitimate wakes stay intact).
    // Cosmetic residue only: a re-fogged unit may sleep mid fade-out (it is invisible anyway). Solo vanilla.
    [HarmonyPatch(typeof(EntityFader), nameof(EntityFader.Visible), MethodType.Setter)]
    internal static class EntityFader_Visible_NoSimWake_Patch
    {
        private static bool s_loggedActive;

        private static void Prefix(EntityFader __instance, out float? __state)
        {
            __state = null;
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return;
                var view = __instance.Entity as UnitEntityView;
                var data = view != null ? view.EntityData : null;
                if (data != null)
                {
                    __state = data.AwakeTimer;
                    if (!s_loggedActive)
                    {
                        s_loggedActive = true;
                        MultiplayerStabilityMain.Log("[DetSleep] FaderWakeCancel active -- fog-dissolve fades no longer write the sim awake timer in multiplayer.");
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void Postfix(EntityFader __instance, float? __state)
        {
            if (__state == null)
                return;
            try
            {
                var view = __instance.Entity as UnitEntityView;
                var data = view != null ? view.EntityData : null;
                if (data != null)
                    data.AwakeTimer = __state.Value;
            }
            catch (Exception)
            {
            }
        }
    }
}

// =====================================================================================================
// Multiplayer Stability -- co-op stability fixes for Rogue Trader, built on the 2026-07-02 netcode
// investigation (C:\Deathwatch Mod\Multiplayer Stability). Separate mod from Deathwatch.
//
// PEER-COMPATIBILITY (three categories -- see MOD-PLAN.md Doctrine for the full list):
//   Subset-safe:          diagnostics + UI-only fixes + the transfer ack pump (any subset of machines).
//   Negotiated protocol:  Steam P2P transfer, Sequenced Locks, Photon window boost (self-gate on
//                         every-peer-modded and engage only then).
//   Exact parity required: EVERY RNG- or simulation-changing prevention fix. Mixed installs range from
//                         useless to actively desync-causing depending on the fix -- until the 0.9
//                         session-latched compatibility gate ships, run the IDENTICAL build on every machine.
//
// Components:
//   1. TRANSFER BOOSTER (TransferBooster.cs) -- faster co-op save transfers over Photon: fast ack pump
//      during transfers + larger chunks/window when every player in the room runs this mod.
//   2. STEAM P2P TRANSFER (SteamP2P.cs + SteamSaveTransfer.cs) -- routes the bulk save bytes over a
//      direct Steam connection (ICE NAT-punch, SDR fallback) with an adaptive send-rate controller;
//      Photon stays the control plane and the fallback path. ~8x measured speedup.
//   3. DESYNC DIAGNOSIS (DesyncWatch.cs) -- logs every desync episode with tick + diverged state bucket
//      + per-peer hashes + which RNG streams moved; re-arms the once-per-session warning. NO auto-resync
//      by design: resyncing stays the player's choice (fast now, via the lobby Launch button). Suppresses the
//      vanilla resync DIALOG for confirmed randomState-only flaps during loading/cutscene transitions (still
//      logged), re-showing it only if they graduate to another bucket or persist -- so the dialog stays meaningful.
//   4. WEATHER RNG FIX (WeatherRngFix.cs) -- first root-cause desync fix: stops weather VFX draining the
//      hashed Weather random stream once per rendered frame.
//   5. PROJECTILE RNG FIX (ProjectileRngFix.cs) -- the burst-fire desync: makes the projectile aim-bone
//      pick deterministic instead of a view-conditional draw from the hashed Projectiles stream.
//   6. SEQUENCED LOCKS (SequencedLocks.cs) -- fixes the vanilla co-op loading-barrier race that hangs a
//      client at 100% while the host is already in the area, by tagging each barrier announcement with a
//      per-session ordinal (active only when all players run this mod; it changes the code-8 wire format).
//   7. DETERMINISTIC SLEEP (DeterministicSleep.cs) -- in multiplayer, the awake-unit census is rebuilt
//      from synchronized state only (combat/dying/cutscene-held units always awake; idle trash sleeps by
//      synced DISTANCE instead of the client-local camera/fog), in deterministic order. Structural fix for
//      the awake-set desync class: death timing, ability target counts, turn forks, combat-join membership,
//      and cutscene pause/resume skew (scripted unit spawns firing ticks apart across clients).
//   8. FOG GATE FIX (FogGateFix.cs) -- in multiplayer, the three MECHANICS decisions that read a client-local
//      visibility flag (area-effect membership + NPC combat-start read IsInFogOfWar; LOSGetter reads
//      IsVisibleForPlayer) drop that term so all clients gate on synced world state (LOS geometry). Root-cause
//      fix for the cutscene->combat-start one-entity forks and the Solomorne-eye LOS buff apply/remove fork.
//   9. DASH DELIVERY FIX (DashDeliveryFix.cs) -- dash-through abilities (Macabre Dance etc.) deliver their
//      path effects to the full precomputed target set deterministically, instead of gating each target on
//      the caster's frame-timed mid-dash view position (which let one client miss a target forever).
//  10. PREVIEW GHOST FIX (PreviewGhostFix.cs) -- client-local UI preview units (inventory dolls, level-up
//      plan units) stay subscribed to game events and were receiving combat-start buffs with hashed-stream
//      ids on one machine only (the "+4 at every combat start" forks). Fact attaches to previews now run
//      hashed-stream-safe, and previews are excluded from aura membership in multiplayer.
//  11. LEAK DETECTOR (LeakDetector.cs) -- PROACTIVE keystone. One prefix on Rand.Get() (the universal
//      chokepoint for every hashed RNG draw + uuid mint) logs any draw of a hashed PFStatefulRandom stream
//      that fires OUTSIDE a deterministic sim tick -- a latent desync, named on ONE machine with no desync
//      needed (works solo). Log-only; the fallback is non-deterministic so we never auto-divert (that would
//      manufacture desyncs on false positives). This is how we get AHEAD of the leak class instead of
//      diffing two logs after the fact.
//  12. PREVIEW RULEBOOK GUARD (PreviewRulebookGuard.cs) -- the rulebook-HANDLER half of the ghost fix
//      (PreviewGhostFix = the uuid-mint half). In MP, a preview-unit ghost's global rulebook handlers were
//      firing during real combat and forking the RuleSystem stream (the burst-attack desync). ONE cold patch
//      on RulebookEventBus.Subscribe(global) skips preview-owned GLOBAL registrations at the source, via the
//      subscriber's proxy owner. Fail-open, MP-only.
//  13. LOCAL TIME-SCALE FIX (LocalTimeScaleFix.cs) -- the two client-local PlayerTimeScale writers (hashed
//      GameTime forks): the fog-gated 16x AI-turn fast-forward is ALWAYS 1x in MP (v0.8.15 -- the frustum
//      substitution was withdrawn: IsInCameraFrustum tests LOCAL renderer bounds, so it is a proxy, not
//      deterministic), and the local slow-mo pause-bind hold is neutralized in MP.
//  14. DETERMINISTIC ORDER FIX (DeterministicOrderFix.cs) -- FindUnitsInRange results sorted by UniqueId in
//      MP (the engine's own ByIdComparison, as its sibling FindUnitsInShape already does): same hashed RNG
//      draw no longer resolves to DIFFERENT victims per machine (Psychic Phenomena, ricochet, crossfire).
//  15. PROJECTILE POSITION FIX (ProjectilePositionFix.cs) -- the geometry half of the projectile family
//      (Solasta-doctrine catch): in MP, projectile target points come from entity/grid state via the
//      engine's own no-view fallbacks, never live view-bone transforms -- closes the ricochet/push geometry
//      hole ProjectileRngFix left, the starship hull-point sibling, and the attack-line random-locator draw.
//  16. DIALOG RNG FIX (DialogRngFix.cs) -- the dialogue UI's answer-preview getters (SkillChecks /
//      SkillChecksDC) draw the hashed DialogSystem stream at view time (CharacterSelection.SelectUnit Random
//      + CueSelection.Select Random), forking randomState mid-conversation (captures 0.8.7 + 0.8.8). ONE
//      guard: both preview getters hold DisableStatefulRandomContext for their whole body in MP (SEMANTIC
//      -- preview-by-definition, valid even in-tick; the failed v0.8.8 timing gate is documented in-file).
//      The real SelectAnswer acting-unit pick and in-tick cue advancement stay hashed + synced. (A global
//      deterministic CueSelection guard was withdrawn in v0.8.15 -- it changed real narrative selection.)
//  17. IDLE ANIMATION RNG FIX (IdleAnimationRngFix.cs) -- the Animation3 flapper class at its source:
//      AnimationManager.StatefulRandom maps idle-variety draws (micro/variant idle triggers, speed jitter)
//      to the HASHED Animation3 stream on the view clock; the engine's own AnimationIdle stream is the
//      designated non-hashed one. In MP the four idle sites reroute to AnimationIdle (transpiler getter
//      swap); dodge/special-attack variant picks deliberately untouched pending their own audit.
//  18. ACTION-BAR ROLE SPAM FIX (ActionBarRoleSpamFix.cs) -- on player-leave, the engine raises a role
//      event per controlled entity (~1,500) and every action-bar slot refreshes on ALL of them, NRE-ing on
//      unitless slots: ~18,000 exception stacks in seconds, unbounded GameLogFull growth (600 MB tester
//      logs). Prefix filters each slot to its OWN unit's events and skips unitless slots. v0.8.16 extends
//      the unitless skip to the per-player room events (HandlePlayerEnteredRoom/LeftRoom -- the residual
//      425-stack storm in the 0.8.14 capture). UI-only.
//      (Batch context: FogGateFix gained three sites -- movement 8x, awareness rolls, ricochet candidates --
//      and DeterministicSleep gained the combat-capable census rule, the corpse reveal-flag re-assert, and
//      the EntityFader wake-cancel. Channel-B audit, 2026-07-09.)
//
// Loading-speed work lives in the separate FasterLoadTimes mod (Assets\Modifications\FasterLoadTimes) --
// it is not co-op-specific and is safe on any subset of a session's machines.
//
// Open backlog (see MOD-PLAN.md): the space-combat sibling of the projectile fix
// (Projectile.GetTargetPointForStarship has the same conditional-draw shape) and the DESYNC-HAZARDS set,
// as a confirming capture names them. (Mod-parity is enforced manually, not by a launch gate.)
// =====================================================================================================
using System.Reflection;
using HarmonyLib;
using Kingmaker.Modding;                          // OwlcatModification, OwlcatModificationEnterPoint

namespace MultiplayerStability
{
    public static class MultiplayerStabilityMain
    {
        // Must match Manifest.UniqueName in MultiplayerStability.asset: it is the identity string other
        // clients see in the Photon mod-list property ("m"), which TransferBooster compares against.
        internal const string UniqueName = "MultiplayerStability";

        public static OwlcatModification Modification { get; private set; }

        [OwlcatModificationEnterPoint]
        public static void Initialize(OwlcatModification modification)
        {
            Modification = modification;
            Log("[Init] Enter point reached; bootstrapping Harmony.");
            var harmony = new Harmony(modification.Manifest.UniqueName);
            // Per-class patching with isolation -- NOT a blanket PatchAll. PatchAll processes patch classes
            // sequentially and a single throw (e.g. a TargetMethods that resolves ZERO methods after a game
            // update -- Harmony throws on target-less classes) aborts EVERYTHING after it, including the
            // manual Wire() calls below: the transfer stack would silently die and saves fall back to
            // vanilla speed with no crash. Each class now fails alone, loudly.
            int patched = 0, failed = 0;
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                try
                {
                    var processor = harmony.CreateClassProcessor(type);
                    if (processor.Patch() != null)
                        patched++;
                }
                catch (System.Exception e)
                {
                    failed++;
                    Log("[Init][ERR] patch class " + type.Name + " failed (component inert, others unaffected): " + e.Message);
                }
            }
            SafeWire("SteamSaveTransfer", () => SteamSaveTransfer.Wire());
            SafeWire("DesyncWatch", () => DesyncWatch.Wire());
            SafeWire("WeatherRngFix", () => WeatherRngFix.Wire(harmony));
            SafeWire("LeakDetector", () => LeakDetector.Wire(harmony));        // proactive out-of-tick hashed-draw detector (patch early, before gameplay JIT)
            SafeWire("PreviewRulebookGuard", () => PreviewRulebookGuard.Wire(harmony)); // block preview-ghost global rulebook subscriptions (registration-time guard; patch early, before gameplay JIT)
            Log("[Init] Patches applied (" + patched + " classes" + (failed > 0 ? ", " + failed + " FAILED" : "") + ").");
        }

        private static void SafeWire(string name, System.Action wire)
        {
            try
            {
                wire();
            }
            catch (System.Exception e)
            {
                Log("[Init][ERR] " + name + ".Wire failed (component inert, others unaffected): " + e.Message);
            }
        }

        public static void Log(string msg)
        {
            Modification?.Logger.Log("[MPStability] " + msg);
        }
    }
}

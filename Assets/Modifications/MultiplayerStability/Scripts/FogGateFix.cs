// Fog gate fix -- mechanics decisions must not read client-local fog-of-war in multiplayer.
//
// Entity.IsInFogOfWar is a cached flag written by each client's own fog reveal (Entity.cs:346 -- the
// setter is driven from the view layer), so two co-op machines can disagree on it. Six mechanics paths
// read this state:
//   1. AreaEffectEntity.ShouldUnitBeInside (aura membership and buff facts).
//   2. UnitCombatJoinController.ShouldStartCombat (NPC combat entry).
//   3. LOSGetter.GetBaseValue (buff apply/remove; keeps the separate HasLOS check).
//   4. UnitMovementAgentBase.TickMovement (8x movement speed and heading snap).
//   5. PartyAwarenessController.Tick (awareness rolls, XP, and trap triggers).
//   6. RicochetHelper.GetPossibleRicochetTargets (candidate filtering).
//
// Fix: each targeted site drops this one convicted client-local term. Fog readers treat units as not
// fogged; the LOS getter treats in-game units as visible. This does not make each whole method
// deterministic: trigger-fed CanBeInRange and area membership, View/ViewTransform awareness inputs,
// movement-agent state, and ricochet candidate discovery remain separate risks. The patch's claim is
// deliberately narrow: fog/visibility no longer adds another branch at these six sites.
//
// TurnController.SetTime uses a separate always-1x multiplayer policy in LocalTimeScaleFix.cs.
// The v0.9 compatibility decision enables these predicate changes only under exact-build parity.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Kingmaker.Controllers.Combat;
using Kingmaker.Controllers.MapObjects;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Entities.Base;
using Kingmaker.Networking;
using Kingmaker.View;

namespace MultiplayerStability
{
    [HarmonyPatch]
    internal static class FogGateFix
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var inside = AccessTools.Method(typeof(AreaEffectEntity), "ShouldUnitBeInside",
                new[] { typeof(BaseUnitEntity) });
            var start = AccessTools.Method(typeof(UnitCombatJoinController), "ShouldStartCombat");
            // LOSGetter is internal (not referenceable by type) -- resolve by name.
            var losType = AccessTools.TypeByName("Kingmaker.LOSGetter");
            var los = losType != null ? AccessTools.Method(losType, "GetBaseValue") : null;
            // Channel-B audit batch 2 sites (all the same never-fogged-in-MP policy):
            //  - TickMovement: fog-gated 8x movement speed + instant heading snap in TB mode; Position is
            //    hashed, so a fog-disputed unit's whole move diverges per tick (base method only -- the
            //    Ship/Continuous/StarSystem overrides contain no fog reads).
            //  - PartyAwarenessController.Tick: the whole awareness pass is fog-gated; one-sided RuleSystem
            //    D100 draws + hashed IsAwarenessCheckPassed/roll-rank writes + trap triggers. The `View ==
            //    null` term beside it is a null-guard and stays untouched.
            //  - RicochetHelper.GetPossibleRicochetTargets: fog filters the ricochet candidate list, so one
            //    machine can draw the hashed Mechanics pick + execute an extra attack line the other skips.
            var move = AccessTools.Method(typeof(UnitMovementAgentBase), "TickMovement");
            var aware = AccessTools.Method(typeof(PartyAwarenessController), "Tick");
            var ricochetType = AccessTools.TypeByName("Kingmaker.RicochetHelper");
            var ricochet = ricochetType != null ? AccessTools.Method(ricochetType, "GetPossibleRicochetTargets") : null;
            var targets = new[] { inside, start, los, move, aware, ricochet };
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] == null)
                    throw new MissingMethodException(
                        "FogGate target discovery failed at index " + i + "; no sites were selected.");
            }
            return targets;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);
            var fogRepl = AccessTools.Method(typeof(FogGateFix), nameof(FogForMechanics));
            var visRepl = AccessTools.Method(typeof(FogGateFix), nameof(VisibleForMechanics));
            bool expectsVisible = original.DeclaringType?.Name == "LOSGetter";
            var expectedGetter = expectsVisible
                ? AccessTools.PropertyGetter(typeof(Entity), nameof(Entity.IsVisibleForPlayer))
                : AccessTools.PropertyGetter(typeof(Entity), nameof(Entity.IsInFogOfWar));
            int expected = original.DeclaringType == typeof(UnitMovementAgentBase) ? 2 : 1;
            var matches = new List<CodeInstruction>();
            foreach (var ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo mi)
                {
                    if (mi == expectedGetter)
                        matches.Add(ci);
                }
            }

            MethodInfo replacement = expectsVisible ? visRepl : fogRepl;
            if (matches.Count != expected || expectedGetter == null || replacement == null)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[FogGate][ERR] " + original.DeclaringType?.Name + "." + original.Name
                    + ": expected " + expected + " " + (expectsVisible ? "visibility" : "fog")
                    + " getter(s), found " + matches.Count + "; method left unchanged.");
                return code;
            }

            for (int i = 0; i < matches.Count; i++)
            {
                matches[i].opcode = OpCodes.Call;
                matches[i].operand = replacement;
            }
            MultiplayerStabilityMain.LogNoThrow(
                "[FogGate] " + original.DeclaringType?.Name + "." + original.Name
                + ": replaced exactly " + matches.Count + " client-local getter(s).");
            return code;
        }

        // In multiplayer every client answers "not fogged" so the mechanics gate opens identically
        // everywhere; solo defers to the real client-local flag (vanilla behaviour exactly).
        public static bool FogForMechanics(Entity entity)
        {
            return !MultiplayerCompatibility.SimulationFixesEnabled && entity.IsInFogOfWar;
        }

        // For an inclusion term (`&& unit.IsVisibleForPlayer`). IsVisibleForPlayer = !IsInFogOfWar && View
        // != null && View.IsVisible && IsInGame -- three client-local terms (fog + render) plus the SYNCED
        // IsInGame. In MP we drop only the client-local part and KEEP IsInGame (deterministic), so a
        // not-in-game unit still can't pass; the HasLOS geometry check beside the term remains separate.
        // Solo defers to the real flag (vanilla).
        public static bool VisibleForMechanics(Entity entity)
        {
            return MultiplayerCompatibility.SimulationFixesEnabled
                ? entity.IsInGame
                : entity.IsVisibleForPlayer;
        }
    }
}

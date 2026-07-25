// Fog gate fix -- mechanics decisions must not read client-local fog-of-war in multiplayer.
//
// Entity.IsInFogOfWar is a cached flag written by each client's own fog reveal (Entity.cs:346 -- the
// setter is driven from the view layer), so two co-op machines can disagree on it. Six mechanics paths
// read this state:
//   1. AreaEffectEntity.ShouldUnitBeInside (aura membership and buff facts).
//   2. UnitCombatJoinController.ShouldStartCombat (NPC combat entry).
//   3. LOSGetter.GetBaseValue (buff apply/remove; keeps the deterministic HasLOS check).
//   4. UnitMovementAgentBase.TickMovement (8x movement speed and heading snap).
//   5. PartyAwarenessController.Tick (awareness rolls, XP, and trap triggers).
//   6. RicochetHelper.GetPossibleRicochetTargets (candidate filtering).
//
// Fix: in multiplayer, every targeted site drops the client-local term. Fog readers treat units as NOT
// fogged (get_IsInFogOfWar -> FogForMechanics); the LOS getter treats units as visible (get_IsVisibleFor
// Player -> VisibleForMechanics, keeping the synced IsInGame term) so only deterministic gates remain.
// Fog/render stays a presentation concept. Solo is untouched (both helpers defer to the real flag outside
// MP). Transpiler call-swap on the getters, same pattern as ProjectileRngFix; patched at mod init (before
// first gameplay JIT). Internal types (LOSGetter, RicochetHelper) are resolved by name.
//
// TurnController.SetTime uses a separate always-1x multiplayer policy in LocalTimeScaleFix.cs.
// Exact parity is required because a mixed install evaluates different mechanics predicates.
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
            if (inside != null)
                yield return inside;
            else
                MultiplayerStabilityMain.Log("[FogGate][ERR] AreaEffectEntity.ShouldUnitBeInside not found -- site unpatched.");
            if (start != null)
                yield return start;
            else
                MultiplayerStabilityMain.Log("[FogGate][ERR] UnitCombatJoinController.ShouldStartCombat not found -- site unpatched.");
            if (los != null)
                yield return los;
            else
                MultiplayerStabilityMain.Log("[FogGate][ERR] LOSGetter.GetBaseValue not found -- site unpatched.");
            if (move != null)
                yield return move;
            else
                MultiplayerStabilityMain.Log("[FogGate][ERR] UnitMovementAgentBase.TickMovement not found -- site unpatched.");
            if (aware != null)
                yield return aware;
            else
                MultiplayerStabilityMain.Log("[FogGate][ERR] PartyAwarenessController.Tick not found -- site unpatched.");
            if (ricochet != null)
                yield return ricochet;
            else
                MultiplayerStabilityMain.Log("[FogGate][ERR] RicochetHelper.GetPossibleRicochetTargets not found -- site unpatched.");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var fogRepl = AccessTools.Method(typeof(FogGateFix), nameof(FogForMechanics));
            var visRepl = AccessTools.Method(typeof(FogGateFix), nameof(VisibleForMechanics));
            int swapped = 0;
            foreach (var ci in instructions)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo mi)
                {
                    // Both getters are instance bool(this) -> our static bool(Entity) has the same stack
                    // transition (pop the entity ref, push bool), so a plain operand swap is valid.
                    MethodInfo repl = null;
                    if (mi.Name == "get_IsInFogOfWar")
                        repl = fogRepl;
                    else if (mi.Name == "get_IsVisibleForPlayer")
                        repl = visRepl;
                    if (repl != null)
                    {
                        yield return new CodeInstruction(OpCodes.Call, repl) { labels = ci.labels, blocks = ci.blocks };
                        swapped++;
                        continue;
                    }
                }
                yield return ci;
            }
            MultiplayerStabilityMain.Log("[FogGate] " + original.DeclaringType?.Name + "." + original.Name
                + ": " + swapped + " client-local visibility read(s) made multiplayer-safe"
                + (swapped == 0 ? " -- PATTERN NOT FOUND, vanilla behaviour in effect" : "") + ".");
        }

        // In multiplayer every client answers "not fogged" so the mechanics gate opens identically
        // everywhere; solo defers to the real client-local flag (vanilla behaviour exactly).
        public static bool FogForMechanics(Entity entity)
        {
            return !NetworkingManager.IsMultiplayer && entity.IsInFogOfWar;
        }

        // For an inclusion term (`&& unit.IsVisibleForPlayer`). IsVisibleForPlayer = !IsInFogOfWar && View
        // != null && View.IsVisible && IsInGame -- three client-local terms (fog + render) plus the SYNCED
        // IsInGame. In MP we drop only the client-local part and KEEP IsInGame (deterministic), so a
        // not-in-game unit still can't pass; the HasLOS geometry check beside the term stays the real gate.
        // Solo defers to the real flag (vanilla).
        public static bool VisibleForMechanics(Entity entity)
        {
            return NetworkingManager.IsMultiplayer ? entity.IsInGame : entity.IsVisibleForPlayer;
        }
    }
}

// Charge-path cache DIAGNOSTIC -- log-only (Codex round 33; tester report: "charged, attacked, he parried,
// desync -- my char and the enemy stood on the SAME TILE"; ChargeBuff appeared 12-13 ticks before both
// first mismatches).
//
// Suspected mechanism (decompile-verified): PathfindingService.FindPathChargeTB_Blocking (:727) tries
// FindFullCachedPath, then FindPartialCachedPath, then ComputeAndCachePath. The PARTIAL lookup matches on
// caster + origin + ignoreBlockers ONLY -- no destination key and NO TARGET ENTITY -- then finds the
// destination as a node INDEX inside a cached path and cuts there. Local aiming PREVIEWS feed the same
// cache, so a path cached under different target occupancy can be cut at the enemy's occupied node and
// reused on the CONTROLLING client only; charge delivery then unconditionally writes
// context.Caster.Position = lastNode.Vector3Position (AbilityCustomDirectMovement :313) -> caster lands ON
// the target's tile on one peer: exactly the reported symptom. Corroboration: Dark Heresy's newer engine
// REMOVED the partial lookup entirely and requires target-identity match on full-cache hits
// (DH PathfindingService :713).
//
// SINCE v0.8.30 THIS FILE CARRIES THE FIX (Codex round 34 -- conviction from symptom + mechanism + Dark
// Heresy corroboration, no capture needed): in MP, partial-cache reuse is DISABLED (prefix returns null);
// exact target-checked hits stay cached; unmatched paths recompute; solo untouched. This is a
// SIMULATION-CHANGING fix: EXACT PARITY REQUIRED (see MOD-PLAN doctrine) -- a mixed install changes which
// path one peer charges along.
//
// The resolution diagnostic remains as the fix's TRIPWIRE, with honest epistemics (Codex round 35): the
// prefix and postfix live in the SAME patch class, so silence alone is inconclusive (a failed install
// removes both). Verification of the fix requires all three: (1) no [Init][ERR] for Partial_Patch at boot,
// (2) the one-time "[ChargeFix] Active" line, (3) no subsequent "partial-cache" source line in MP.
// Diagnostics log every charge-path resolution with SOURCE (exact-cache / partial-cache / computed),
// caster, origin/destination, target id+position, and the resolved path's final node (read reflectively --
// GraphNode lives in the A* assembly). Charges are rare, so every call logs.
using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Entities.Base;
using Kingmaker.Networking;
using Kingmaker.Pathfinding;
using Kingmaker.View;
using UnityEngine;

namespace MultiplayerStability
{
    internal static class ChargePathDiag
    {
        private static string s_lastSource = "none";

        private static void Note(string source)
        {
            s_lastSource = source;
        }

        [HarmonyPatch(typeof(PathfindingService), "FindFullCachedPath",
            typeof(UnitMovementAgentBase), typeof(Vector3), typeof(Vector3), typeof(bool), typeof(MechanicEntity))]
        internal static class Full_Patch
        {
            private static void Postfix(object __result)
            {
                if (__result != null)
                    Note("exact-cache");
            }
        }

        // THE FIX (v0.8.30, Codex round 34 -- conviction threshold met without waiting for the capture): in
        // MP, partial-cache reuse is disabled outright -- the lookup ignores the target entity whose
        // occupancy shaped the cached path (aiming previews feed the same cache), so it can hand ONE client
        // a preview-polluted path cut at the enemy's occupied node, and delivery writes Caster.Position to
        // that node: the reported same-tile landings. Exact hits (caster/origin/destination/target-checked)
        // stay; vanilla recomputes when no exact match exists -- precisely Dark Heresy's newer shape, which
        // removed this lookup entirely. Solo untouched. The diagnostic postfix stays as the fix's validator:
        // a "partial-cache" source line in MP would mean the fix is not holding.
        [HarmonyPatch(typeof(PathfindingService), "FindPartialCachedPath",
            typeof(UnitMovementAgentBase), typeof(Vector3), typeof(Vector3), typeof(bool))]
        internal static class Partial_Patch
        {
            private static bool s_loggedActive;

            private static bool Prefix(ref object __result)
            {
                bool multiplayer;
                try
                {
                    multiplayer = NetworkingManager.IsMultiplayer;
                }
                catch (Exception)
                {
                    return true;                                 // fail-open: vanilla behaviour
                }
                if (!multiplayer)
                    return true;                                 // solo: vanilla partial reuse untouched
                // Once MP is confirmed, the suppression is unconditional -- logging is best-effort and can
                // never route back to the defective lookup (the activation-log-in-fail-open pattern has now
                // bitten three times; rule: activation logs never live inside fail-open trys).
                __result = null;
                LogActiveOnce();
                return false;                                    // no partial reuse: fall through to compute
            }

            private static void LogActiveOnce()
            {
                try
                {
                    if (s_loggedActive)
                        return;
                    MultiplayerStabilityMain.Log("[ChargeFix] Active -- partial charge-path cache reuse disabled in multiplayer (exact hits kept; unmatched paths recompute).");
                    s_loggedActive = true;       // latch AFTER success: a failed log retries later, so the
                                                 // verification signal is never permanently lost (round 36)
                }
                catch (Exception)
                {
                }
            }

            private static void Postfix(object __result)
            {
                if (__result != null)
                    Note("partial-cache");
            }
        }

        [HarmonyPatch(typeof(PathfindingService), "ComputeAndCachePath",
            typeof(UnitMovementAgentBase), typeof(Vector3), typeof(Vector3), typeof(bool), typeof(MechanicEntity))]
        internal static class Computed_Patch
        {
            private static void Postfix(object __result)
            {
                if (__result != null)
                    Note("computed");
            }
        }

        [HarmonyPatch(typeof(PathfindingService), nameof(PathfindingService.FindPathChargeTB_Blocking),
            typeof(UnitMovementAgentBase), typeof(Vector3), typeof(Vector3), typeof(bool), typeof(MechanicEntity))]
        internal static class Entry_Patch
        {
            private static void Prefix()
            {
                s_lastSource = "none";
            }

            private static void Postfix(UnitMovementAgentBase agent, Vector3 origin, Vector3 destination,
                object targetEntity, object __result)
            {
                try
                {
                    if (!NetworkingManager.IsMultiplayer)
                        return;
                    var sb = new System.Text.StringBuilder(224);
                    sb.Append("[ChargeDiag] path source=").Append(s_lastSource)
                      .Append(" tick=").Append(Game.Instance.RealTimeController.CurrentNetworkTick)
                      .Append(" caster=").Append(agent != null && agent.Unit != null ? agent.Unit.UniqueId : "?")
                      .Append(" origin=").Append(origin.ToString("F1"))
                      .Append(" dest=").Append(destination.ToString("F1"));
                    var target = targetEntity as Entity;
                    sb.Append(" target=").Append(target != null ? target.UniqueId : "null");
                    try
                    {
                        if (target != null)
                        {
                            var posProp = AccessTools.Property(target.GetType(), "Position");
                            var pos = posProp != null ? posProp.GetValue(target) : null;
                            if (pos is Vector3 v)
                                sb.Append(" targetPos=").Append(v.ToString("F1"));
                        }
                    }
                    catch (Exception) { sb.Append(" targetPos=?"); }
                    sb.Append(" pathEnd=").Append(PathEnd(__result));
                    MultiplayerStabilityMain.Log(sb.ToString());
                }
                catch (Exception)
                {
                    // log-only diagnostic: never interfere
                }
            }
        }

        // The path node list references the A* assembly -- walk it reflectively: __result.path is a
        // List<GraphNode>; the last node's Vector3Position is the landing point delivery will write.
        private static string PathEnd(object path)
        {
            try
            {
                if (path == null)
                    return "null";
                var nodes = AccessTools.Field(path.GetType(), "path")?.GetValue(path) as IList;
                if (nodes == null || nodes.Count == 0)
                    return "empty";
                var last = nodes[nodes.Count - 1];
                var pos = AccessTools.Property(last.GetType(), "Vector3Position")?.GetValue(last);
                return pos is Vector3 v ? v.ToString("F1") : "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}

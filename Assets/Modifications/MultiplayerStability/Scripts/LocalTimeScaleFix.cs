// Local time-scale fixes for the two places a client-local input writes TimeController.PlayerTimeScale,
// which multiplies the fixed 50ms step into m_DeltaTime and Player.GameTime. GameTime is [JsonProperty] and
// hashed (Player root), so a one-sided time-scale creates a permanent hash fork and changes simulation
// timing while it differs. CameraFollowTimeScale is synchronized through a MemoryPackable GameCommand;
// these two writers are not.
// (Channel-B audit ranks 1 and 14; both verified in the decompile before building.)
//
//  1. TurnController.SetTime (TurnController.cs:831): during an AI turn, PlayerTimeScale = 16f when the
//     current unit is fogged on that client (fast-forward hidden turns), else AiAbilitySpeedMod (a constant
//     1f). One tick of fog disagreement = 800ms vs 50ms of hashed GameTime -- fires on every TB AI turn near
//     a fog boundary. Fix: transpile the get_IsInFogOfWar read to HiddenForAiTurnSpeed: in MP, always 1x
//     (never fast-forward). A frustum-union substitution was tried first (v0.8.1) and withdrawn (v0.8.15):
//     IsInCameraFrustum culls against local View.RenderersBounds, so it is not deterministic.
//     Solo keeps the real fog flag (vanilla).
//  2. UnpauseController.Tick (UnpauseController.cs:23): holding the local pause-invert bind writes
//     PlayerTimeScale = 0.6f every sim tick -- never synced. Surviving divergence window is StarSystem mode
//     (elsewhere TurnController overwrites it first). Fix: transpile the 0.6f constant to a helper returning
//     1f in MP (the synced pause itself, and everything else in the method, is untouched); solo vanilla.
//
// Both transpilers are fail-open: pattern not found -> original IL unchanged + a loud log line.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Kingmaker.Controllers;
using Kingmaker.Controllers.TurnBased;
using Kingmaker.EntitySystem.Entities.Base;
using Kingmaker.Networking;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(TurnController), "SetTime")]
    internal static class TurnController_SetTime_DeterministicAiSpeed_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var replacement = AccessTools.Method(typeof(TurnController_SetTime_DeterministicAiSpeed_Patch),
                nameof(HiddenForAiTurnSpeed));
            int swapped = 0;
            foreach (var ci in instructions)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call)
                    && ci.operand is MethodInfo mi && mi.Name == "get_IsInFogOfWar")
                {
                    // Same stack transition: consumes the entity ref, pushes bool.
                    yield return new CodeInstruction(OpCodes.Call, replacement) { labels = ci.labels, blocks = ci.blocks };
                    swapped++;
                    continue;
                }
                yield return ci;
            }
            MultiplayerStabilityMain.Log("[TimeScaleFix] TurnController.SetTime: " + swapped
                + " fog read(s) made deterministic"
                + (swapped == 0 ? " -- PATTERN NOT FOUND, vanilla behaviour in effect" : "") + ".");
        }

        // MP: NEVER fast-forward -- the conservative always-1x policy (v0.8.15). The v0.8.1 version
        // substituted !IsInCameraFrustum believing the frustum union deterministic; the frustum test
        // actually culls against LOCAL View.RenderersBounds (EntitiesInCameraFrustumController :92), so
        // camera-edge units could still fork the hashed GameTime 16x-vs-1x. The final policy removes
        // this renderer-state dependency.
        // Cost: hidden AI turns run at 1x in co-op -- cosmetic pacing, the designed fallback from day one.
        // Solo: the real client-local fog flag (vanilla exactly).
        public static bool HiddenForAiTurnSpeed(Entity entity)
        {
            return !NetworkingManager.IsMultiplayer && entity.IsInFogOfWar;
        }
    }

    [HarmonyPatch(typeof(UnpauseController), nameof(UnpauseController.Tick))]
    internal static class UnpauseController_Tick_NoLocalSlowMo_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var replacement = AccessTools.Method(typeof(UnpauseController_Tick_NoLocalSlowMo_Patch), nameof(SlowMoScale));
            int swapped = 0;
            foreach (var ci in instructions)
            {
                if (ci.opcode == OpCodes.Ldc_R4 && ci.operand is float f && f == 0.6f)
                {
                    // Same stack transition: pushes a float.
                    yield return new CodeInstruction(OpCodes.Call, replacement) { labels = ci.labels, blocks = ci.blocks };
                    swapped++;
                    continue;
                }
                yield return ci;
            }
            MultiplayerStabilityMain.Log("[TimeScaleFix] UnpauseController.Tick: " + swapped
                + " local slow-mo constant(s) neutralized in multiplayer"
                + (swapped == 0 ? " -- PATTERN NOT FOUND, vanilla behaviour in effect" : "") + ".");
        }

        public static float SlowMoScale()
        {
            return NetworkingManager.IsMultiplayer ? 1f : 0.6f;
        }
    }
}

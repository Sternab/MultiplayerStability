// Idle-animation RNG fix for the Animation3 transition class (three-machine capture 0.8.10; targets
// and behavior verified against the decompiled source before implementation).
//
// AnimationManager.StatefulRandom (AnimationManager.cs:32) maps to PFStatefulRandom.Visuals.Animation3,
// which is in the serialized randomState set. Unit idle variety draws it on the view/animation
// clock: micro-idle triggers, variant-idle rerolls, idle speed jitter. Idle timing is inherently
// client-local (who is on-screen, animator update cadence), so two machines can draw the same values a few
// ticks apart. This creates transient randomState skew that normally converges, but recurring draws can
// make it persist (capture 0.8.10: P3 drew 34A6B1A7 @1662981, host/P2 @1662994) while every other
// stream and entity hash agreeing.
//
// PFStatefulRandom.Visuals.AnimationIdle is excluded from the serialized set
// (PFStatefulRandom.cs:97) and is the non-hashed idle stream.
//
// Fix: in multiplayer, reroute only the idle call graph to AnimationIdle. A transpiler on the four idle
// sites swaps the get_StatefulRandom call for a helper (solo: the real property, vanilla byte-identical;
// MP: AnimationIdle). Idle variety is preserved per machine, and the idle path no longer advances the
// hashed Animation3 stream.
//
//   1. UnitAnimationManager.TickIdleVariants  (draws :819/:824/:827/:853/:858 -- triggers + speed jitter)
//   2. UnitAnimationManager.OnAnimationSetChanged (:1071 -- retrigger tracker rebuild)
//   3. UnitAnimationActionMicroIdle.OnStart   (:45 -- variant pick)
//   4. UnitAnimationActionVariantIdle.OnStart (:42/:50/:98/:107 -- override chances + variant pick)
//
// Deliberately NOT touched (verified they share the same property): UnitAnimationActionDodge
// (:103) and UnitAnimationActionSpecialAttack (:180) -- combat animation-variant picks that may deserve the
// same treatment but need their own audit (they fire inside synced combat execution, where the hashed draw
// is symmetric and harmless unless proven otherwise). Do not widen this patch to them without a capture.
//
// Exact parity required: a modded-vs-vanilla pair would advance Animation3 on one machine only.
// The session latch leaves the helper on its vanilla path unless every peer advertises this exact build.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Kingmaker.Networking;
using Kingmaker.Utility.Random;
using Kingmaker.Utility.StatefulRandom;
using Kingmaker.Visual.Animation;
using Kingmaker.Visual.Animation.Kingmaker;
using Kingmaker.Visual.Animation.Kingmaker.Actions;

namespace MultiplayerStability
{
    [HarmonyPatch]
    internal static class IdleAnimationRngFix
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            // EXPLICIT signatures on every target (the 0.8.11 incident): OnStart is AMBIGUOUS by name --
            // the actions inherit base OnStart(AnimationActionHandle) alongside their own
            // OnStart(UnitAnimationActionHandle) -- so a name-only lookup threw AmbiguousMatchException on
            // every machine and, before the 0.8.12 per-class init isolation, aborted the blanket PatchAll
            // with the transfer stack still unwired (saves silently fell back to vanilla 0.22 MB/s).
            var labels = new[]
            {
                "UnitAnimationManager.TickIdleVariants(float)",
                "UnitAnimationManager.OnAnimationSetChanged()",
                "UnitAnimationActionMicroIdle.OnStart(UnitAnimationActionHandle)",
                "UnitAnimationActionVariantIdle.OnStart(UnitAnimationActionHandle)",
            };
            var sites = new[]
            {
                AccessTools.Method(typeof(UnitAnimationManager), "TickIdleVariants", new[] { typeof(float) }),
                AccessTools.Method(typeof(UnitAnimationManager), "OnAnimationSetChanged", Type.EmptyTypes),
                AccessTools.Method(typeof(UnitAnimationActionMicroIdle), "OnStart", new[] { typeof(UnitAnimationActionHandle) }),
                AccessTools.Method(typeof(UnitAnimationActionVariantIdle), "OnStart", new[] { typeof(UnitAnimationActionHandle) }),
            };
            for (int i = 0; i < sites.Length; i++)
            {
                if (sites[i] == null)
                    throw new MissingMethodException(
                        labels[i] + " not found; idle RNG patch class inactive.");
            }
            return sites;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);
            var replacement = AccessTools.Method(typeof(IdleAnimationRngFix), nameof(IdleRng));
            var matches = new List<CodeInstruction>();
            foreach (var ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call)
                    && ci.operand is MethodInfo mi && mi.Name == "get_StatefulRandom"
                    && mi.DeclaringType == typeof(AnimationManager))
                    matches.Add(ci);
            }
            int expected = ExpectedCount(original);
            if (matches.Count != expected || replacement == null)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[IdleRng][ERR] " + original.DeclaringType?.Name + "." + original.Name
                    + ": expected " + expected + " StatefulRandom getter(s), found " + matches.Count
                    + "; method left unchanged.");
                return code;
            }
            for (int i = 0; i < matches.Count; i++)
            {
                matches[i].opcode = OpCodes.Call;
                matches[i].operand = replacement;
            }
            MultiplayerStabilityMain.LogNoThrow(
                "[IdleRng] " + original.DeclaringType?.Name + "." + original.Name
                + ": replaced exactly " + matches.Count + " idle RNG getter(s).");
            return code;
        }

        private static int ExpectedCount(MethodBase original)
        {
            if (original.DeclaringType == typeof(UnitAnimationManager))
                return original.Name == "TickIdleVariants" ? 5 : 1;
            if (original.DeclaringType == typeof(UnitAnimationActionVariantIdle))
                return 4;
            return 1;
        }

        // MP: the engine's own designated NON-hashed idle stream (excluded from the serialized set) --
        // idle variety stays random, the hashed stream stays untouched. Solo: the real property, vanilla
        // exactly (including its doll-room switch).
        public static StatefulRandom IdleRng(AnimationManager manager)
        {
            return MultiplayerCompatibility.SimulationFixesEnabled
                ? PFStatefulRandom.Visuals.AnimationIdle
                : manager.StatefulRandom;
        }
    }
}

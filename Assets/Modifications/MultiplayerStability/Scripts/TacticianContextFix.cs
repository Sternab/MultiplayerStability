// Tactician Comfort in Conformity context fix.
//
// TacticalAdvantagePassive is a global RulePerformMomentumChange subscriber. After updating its
// saved momentum remainder and Tactical Advantage buff, it triggers the Comfort in Conformity heal
// through Fact.MaybeContext. A permanent passive can receive the global event while that context is
// not the current mechanics context. MechanicsContext.TriggerRule then tries to construct a
// RuleReason from the stale context and throws "Fact context differs from current context" before
// Rulebook.Trigger runs. RulebookSubscribersList catches exceptions per subscriber, so this does
// not abort later subscribers. It prevents this passive's intended heal after its state writes.
//
// Paired v0.8.23 logs recorded this exception repeatedly for Eogann immediately before an isolated
// Eogann entity fork. A later field report associates Eogann with desyncs after Officer-granted
// extra turns, which all produce positive turn-start momentum events. The old exceptions occurred
// on both peers, so this patch claims only to remove the proven invalid trigger. It could produce a
// state difference if context validity differs between peers, but the available capture does not
// prove that happened. Whether it closes the reported desync still requires paired post-fix
// validation.
//
// In exact-parity multiplayer, valid contexts still take the original TriggerRule path. Only when
// the same validation used by RuleReason fails does the helper use the engine's established
// fact-reason pattern: assign the owning UnitFact as RuleReason, then call Rulebook.Trigger. The
// Tactician arithmetic, buff application, heal amount, target, and all surrounding conditions remain
// vanilla. Solo or incompatible sessions execute the original call. If the method no longer
// contains exactly one matching call, the transpiler leaves it unchanged.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Kingmaker.ElementsSystem;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules.Damage;
using Kingmaker.UnitLogic.FactLogic;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.UnitLogic.Mechanics.Facts;

namespace MultiplayerStability
{
    [HarmonyPatch(typeof(TacticalAdvantagePassive), "OnEventDidTrigger",
        typeof(Kingmaker.RuleSystem.Rules.RulePerformMomentumChange))]
    internal static class TacticianContextFix
    {
        private static bool s_loggedActive;

        public static RuleHealDamage TriggerComfortHeal(
            MechanicsContext context,
            RuleHealDamage rule,
            TacticalAdvantagePassive component)
        {
            if (!MultiplayerCompatibility.SimulationFixesEnabled)
                return context.TriggerRule(rule);

            MechanicEntityFact currentFact;
            if (context != null && ContextDataHelper.TryGetFactByContext(context, out currentFact))
                return context.TriggerRule(rule);

            var fact = component?.Runtime?.Fact as MechanicEntityFact;
            if (fact == null)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[TacticianFix][ERR] owning fact unavailable; using vanilla heal trigger.");
                return context.TriggerRule(rule);
            }

            rule.Reason = fact;
            RuleHealDamage result = Rulebook.Trigger(rule);
            LogActiveOnce();
            return result;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo replacement = AccessTools.Method(
                typeof(TacticianContextFix), nameof(TriggerComfortHeal));
            var matches = new List<int>();

            for (int i = 0; i < code.Count; i++)
            {
                if ((code[i].opcode != OpCodes.Callvirt && code[i].opcode != OpCodes.Call)
                    || !(code[i].operand is MethodInfo method)
                    || !method.IsGenericMethod
                    || method.DeclaringType != typeof(MechanicsContext)
                    || method.Name != nameof(MechanicsContext.TriggerRule))
                    continue;

                Type[] arguments = method.GetGenericArguments();
                if (arguments.Length == 1 && arguments[0] == typeof(RuleHealDamage))
                    matches.Add(i);
            }

            if (matches.Count != 1 || replacement == null)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[TacticianFix][ERR] expected one Comfort heal TriggerRule call, found "
                    + matches.Count + "; method left unchanged.");
                return code;
            }

            int callIndex = matches[0];
            CodeInstruction originalCall = code[callIndex];
            var loadComponent = new CodeInstruction(OpCodes.Ldarg_0);
            loadComponent.labels.AddRange(originalCall.labels);
            originalCall.labels.Clear();
            loadComponent.blocks.AddRange(originalCall.blocks);
            originalCall.blocks.Clear();

            code.Insert(callIndex, loadComponent);
            originalCall.opcode = OpCodes.Call;
            originalCall.operand = replacement;
            MultiplayerStabilityMain.LogNoThrow(
                "[TacticianFix] Comfort heal trigger replaced exactly once; runtime use requires exact parity.");
            return code;
        }

        private static void LogActiveOnce()
        {
            if (s_loggedActive)
                return;
            MultiplayerStabilityMain.LogNoThrow(
                "[TacticianFix] Repaired invalid-context Comfort heal.");
            s_loggedActive = true;
        }
    }
}

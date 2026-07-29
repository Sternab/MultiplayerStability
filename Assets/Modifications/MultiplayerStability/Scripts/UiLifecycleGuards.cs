// UI lifecycle guards for exception families recorded on both peers in the paired v0.9.2
// folder-3 capture.
//
// Rogue Trader keeps several UI subscribers alive while their selected or preview unit is being
// cleared or disposed. Four invalid callbacks were observed:
//   1. SurfaceHUDVM.OnUnitChanged passes a null selection to the Hunt-the-Prey dictionary lookup.
//   2. CharInfoExperienceVM refreshes after its unit has become null or disposed.
//   3. InventoryDollAdditionalStatsVM refreshes from equipment events after its real or preview
//      unit has become null or disposed.
//   4. InventoryDollAdditionalStatsPCView's SetValues subscription dereferences a null unit.
//
// These patches discard only work whose required UI model is already invalid. They are deliberately
// ungated and subset-safe: no synchronized state is written, valid callbacks keep their complete
// vanilla path, and no exception is swallowed. Dark Heresy's newer UI independently removes the
// unsafe additional-stats subscription, handles a null Surface HUD selection without the dictionary
// lookup, and adds a null/disposed check to its experience display. Those differences corroborate
// the lifecycle invariant but are not treated as proof of Owlcat's intent for Rogue Trader.
//
// This component claims prevention of the captured exception stacks. Whether those exceptions caused
// either associated desync remains a post-fix field question.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Kingmaker.Code.UI.MVVM.View.ServiceWindows.Inventory;
using Kingmaker.Code.UI.MVVM.VM.ServiceWindows.CharacterInfo.Sections.LevelClassScores.Experience;
using Kingmaker.Code.UI.MVVM.VM.ServiceWindows.Inventory;
using Kingmaker.Code.UI.MVVM.VM.SurfaceCombat;
using Kingmaker.EntitySystem.Entities;

namespace MultiplayerStability
{
    internal static class UiLifecycleGuards
    {
        internal static bool HasLiveUnit(BaseUnitEntity unit)
        {
            return unit != null && !unit.IsDisposed;
        }
    }

    // Preserve all selection teardown and replacement work in OnUnitChanged. Only replace the one
    // null-unsafe Enumerable.Contains call used by the Hunt-the-Prey dictionary membership test.
    [HarmonyPatch(typeof(SurfaceHUDVM), "OnUnitChanged")]
    internal static class SurfaceHUDVM_OnUnitChanged_NullSelection_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo replacement = AccessTools.Method(
                typeof(SurfaceHUDVM_OnUnitChanged_NullSelection_Patch),
                nameof(ContainsNonNullSelection));
            int replacements = 0;

            foreach (CodeInstruction instruction in code)
            {
                if (!(instruction.operand is MethodInfo method)
                    || method.DeclaringType != typeof(Enumerable)
                    || method.Name != nameof(Enumerable.Contains)
                    || !method.IsGenericMethod
                    || method.GetParameters().Length != 2)
                {
                    continue;
                }

                Type[] genericArguments = method.GetGenericArguments();
                if (genericArguments.Length != 1 || genericArguments[0] != typeof(MechanicEntity))
                    continue;

                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replacements++;
            }

            if (replacement == null || replacements != 1)
            {
                throw new InvalidOperationException(
                    original.DeclaringType?.FullName + "." + original.Name
                    + ": expected one Hunt-the-Prey Enumerable.Contains<MechanicEntity> call, found "
                    + replacements + ".");
            }

            MultiplayerStabilityMain.LogNoThrow(
                "[UILifecycle] SurfaceHUDVM.OnUnitChanged null-selection guard installed.");
            return code;
        }

        public static bool ContainsNonNullSelection(
            IEnumerable<MechanicEntity> keys,
            MechanicEntity selectedUnit)
        {
            return selectedUnit != null && keys.Contains(selectedUnit);
        }
    }

    [HarmonyPatch(typeof(CharInfoExperienceVM), "UpdateData")]
    internal static class CharInfoExperienceVM_UpdateData_LiveUnit_Patch
    {
        private static bool Prefix(CharInfoExperienceVM __instance)
        {
            return UiLifecycleGuards.HasLiveUnit(__instance.Unit.Value);
        }
    }

    [HarmonyPatch(
        typeof(InventoryDollAdditionalStatsVM),
        nameof(InventoryDollAdditionalStatsVM.HandleEquipmentSlotUpdated))]
    internal static class InventoryDollAdditionalStatsVM_Equipment_LiveUnit_Patch
    {
        private static bool Prefix(InventoryDollAdditionalStatsVM __instance)
        {
            return UiLifecycleGuards.HasLiveUnit(__instance.Unit.Value);
        }
    }

    // UpdateData reads both PreviewUnit (for RuleCalculateStatsArmor and related UI calculations) and
    // Unit (for Resolve). Guarding the shared refresh covers the separately captured active-equipment
    // event path without replacing any valid calculation.
    [HarmonyPatch(typeof(InventoryDollAdditionalStatsVM), "UpdateData")]
    internal static class InventoryDollAdditionalStatsVM_UpdateData_LiveUnits_Patch
    {
        private static bool Prefix(InventoryDollAdditionalStatsVM __instance)
        {
            return UiLifecycleGuards.HasLiveUnit(__instance.Unit.Value)
                && UiLifecycleGuards.HasLiveUnit(__instance.PreviewUnit.Value);
        }
    }

    // The unsafe read is in the compiler-generated callback created by SetValues, not in SetValues
    // itself. Resolve it by semantic owner, parameter, and return type instead of relying on the
    // current compiler-generated ordinal.
    [HarmonyPatch]
    internal static class InventoryDollAdditionalStatsPCView_SetValues_NullUnit_Patch
    {
        private static MethodBase TargetMethod()
        {
            MethodInfo[] candidates = AccessTools
                .GetDeclaredMethods(typeof(InventoryDollAdditionalStatsPCView))
                .Where(method =>
                    method.Name.StartsWith("<SetValues>b__", StringComparison.Ordinal)
                    && method.ReturnType == typeof(void)
                    && method.GetParameters().Length == 1
                    && method.GetParameters()[0].ParameterType == typeof(BaseUnitEntity))
                .ToArray();

            if (candidates.Length != 1)
            {
                throw new MissingMethodException(
                    "Expected one InventoryDollAdditionalStatsPCView.SetValues(BaseUnitEntity) "
                    + "generated callback, found " + candidates.Length + ".");
            }

            return candidates[0];
        }

        private static bool Prefix(BaseUnitEntity __0)
        {
            return __0 != null;
        }
    }
}

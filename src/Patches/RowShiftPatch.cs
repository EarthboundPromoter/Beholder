using HarmonyLib;
using System;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// CP4: the Ctrl-pair action-row walk gets composed names. The Ctrl pair
    /// is the game's own D-pad ability-shift — each press runs
    /// setMouseToClosestAbilityButtonAbove/Below (GUIControl), incrementing
    /// the row's own selection index and snapping the mouse onto the button
    /// (native hover follows; the description echo speaks the game's fuller
    /// text behind our terse anchor). Note-only postfixes; the drain composes
    /// "N: label." from the settled index and the state's own ButtonData
    /// hoverText — the unavailability variants (buttonNoSpell etc.) carry
    /// their own words. Targets are full method bodies, outside the inline
    /// hazard class. Seam-gated (WP8).
    /// </summary>
    [HarmonyPatch]
    public static class RowShiftPatch
    {
        [HarmonyPrepare]
        static bool Prepare() => Seams.GUIControl_setMouseToClosestAbilityButtonAbove != null
            && Seams.GUIControl_setMouseToClosestAbilityButtonBelow != null;

        [HarmonyTargetMethods]
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            yield return Seams.GUIControl_setMouseToClosestAbilityButtonAbove;
            yield return Seams.GUIControl_setMouseToClosestAbilityButtonBelow;
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            try
            {
                CombatSpine.NoteRowShift(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[RowShift] {ex.Message}");
            }
        }
    }
}

using HarmonyLib;
using System;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// CP1 forecast note (combat-spec §6): UIActionCounter.update(character,
    /// ability) is the game's own per-frame meeting point of the current
    /// character and the PENDING ability — the targeting states pass their
    /// aimed component, planning passes null (CombatAbilityTargeting.cs:23-26,
    /// CombatSpellTargeting.cs:23-26). Note-only; the drain diffs ability
    /// identity and composes the cost forecast from getTimeCost + the pip
    /// flash rule. A ~30-line method called from the GUI build chain — safely
    /// outside the Mono inline-detour hazard class, and applied at Awake
    /// before any combat GUI has JIT-compiled a call site. Seam-gated (WP8).
    /// </summary>
    [HarmonyPatch]
    public static class ActionCounterPatch
    {
        [HarmonyPrepare]
        static bool Prepare() => Seams.UIActionCounter_update != null;

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.UIActionCounter_update;

        [HarmonyPostfix]
        static void Postfix(object __0, object __1)
        {
            try
            {
                CombatSpine.NoteActionCounter(__0, __1);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[ActionCounter] {ex.Message}");
            }
        }
    }
}

using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Canvas-arrival join (owner ride finding, 2026-08-16). Popups with more
    /// than one navigable zone (the visual-style modal's slider rows vs its
    /// button row; grid popups' grid vs buttons) cross the boundary via
    /// PopUpUIBase.setControllerScrollableUICanvas — a canvas SWITCH, not a
    /// selection-index write — so the selection join never hears it, and
    /// returning to a canvas whose index didn't change also trips the drain's
    /// dedup. Note-only postfix; the drain composes the arrived canvas's
    /// settled focus and speaks it, superseding the frame's selection note.
    /// Seam-gated (WP8).
    /// </summary>
    [HarmonyPatch]
    public static class CanvasSwitchPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            if (Seams.PopUpUIBase_setControllerScrollableUICanvas == null)
            {
                Plugin.Logger?.LogWarning("[CanvasSwitch] setControllerScrollableUICanvas seam missing — zone crossings unvoiced");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.PopUpUIBase_setControllerScrollableUICanvas;

        [HarmonyPostfix]
        static void Postfix(object __0)
        {
            if (__0 != null) Pump.NoteCanvasSwitch(__0);
        }
    }
}

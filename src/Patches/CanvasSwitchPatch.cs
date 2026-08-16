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
    /// </summary>
    [HarmonyPatch]
    public static class CanvasSwitchPatch
    {
        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("PopUpBase+PopUpUIBase");
            if (type == null)
            {
                Plugin.Logger?.LogWarning("[CanvasSwitch] PopUpBase+PopUpUIBase not found — zone crossings unvoiced");
                return null;
            }
            var method = AccessTools.Method(type, "setControllerScrollableUICanvas");
            if (method == null)
            {
                Plugin.Logger?.LogWarning("[CanvasSwitch] setControllerScrollableUICanvas not found");
                return null;
            }
            Plugin.Logger?.LogInfo("[CanvasSwitch] Patching PopUpUIBase.setControllerScrollableUICanvas");
            return method;
        }

        [HarmonyPostfix]
        static void Postfix(object __0)
        {
            if (__0 != null) Pump.NoteCanvasSwitch(__0);
        }
    }
}

using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// The selection join (build-plan WP4). UICanvas.setCurrentSelectedButton(int)
    /// is a trivial private setter inherited by every button control
    /// (UIButtonControlBase : UICanvas), and every selection write path funnels
    /// through it: the native increment/decrement (driven by the controller feed
    /// since WP7), hover-to-selection sync, grid screens, clamping, and
    /// screen-init resets (verified UICanvas.cs:91 and callers, 2026-08-16).
    ///
    /// Note-only: records the instance; the Pump's drain reads the final index at
    /// end of frame (set-then-clamp sequences resolve automatically) and speaks
    /// once per actual index change. Replaces the per-frame GUIControl.update and
    /// UIButtonControlBase.update navigation pollers, NavigationCursor, and the
    /// popup init-suppression window.
    /// </summary>
    [HarmonyPatch]
    public static class SelectionJoinPatch
    {
        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("UICanvas");
            if (type == null)
            {
                Plugin.Logger?.LogError("[Selection] UICanvas not found");
                return null;
            }
            var method = AccessTools.Method(type, "setCurrentSelectedButton");
            if (method == null)
            {
                Plugin.Logger?.LogError("[Selection] setCurrentSelectedButton not found");
                return null;
            }
            Plugin.Logger?.LogInfo("[Selection] Patching UICanvas.setCurrentSelectedButton");
            return method;
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            Pump.NoteSelection(__instance);
        }
    }
}

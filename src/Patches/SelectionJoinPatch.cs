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
    /// once per actual index change. Seam-gated (WP8).
    /// </summary>
    [HarmonyPatch]
    public static class SelectionJoinPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            if (Seams.UICanvas_setCurrentSelectedButton == null)
            {
                Plugin.Logger?.LogError("[Selection] setCurrentSelectedButton seam missing — selection speech disabled");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.UICanvas_setCurrentSelectedButton;

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            Pump.NoteSelection(__instance);
        }
    }
}

using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Inventory filter announcements (owner direction 2026-08-17). The Ctrl
    /// cycle (SkaldIO ability-shift, InventoryBaseState.cs:40-47) and mouse
    /// clicks on the filter buttons both land in the one choke,
    /// FilterControl.setFilterByIndex — note the control and let the drain
    /// diff the settled index. The filters render icons only; the spoken name
    /// transcodes from the game's own icon path ("FilterWeapon" → "Weapon").
    /// Seam-gated (WP8).
    /// </summary>
    [HarmonyPatch]
    public static class FilterAnnouncePatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            if (Seams.FilterControl_setFilterByIndex == null
                || Seams.FilterControl_getFilterIndex == null
                || Seams.FilterControl_getIconPaths == null)
            {
                Plugin.Logger?.LogWarning("[Filter] seams missing — filter changes silent");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.FilterControl_setFilterByIndex;

        [HarmonyPostfix]
        static void Postfix(object __instance) => Pump.NoteFilterChange(__instance);
    }
}

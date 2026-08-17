using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Inventory hover join (owner direction 2026-08-17). The character
    /// inventory is HOVER-driven: the game resolves clicks and its column
    /// state from the cell under the virtual mouse (the segment's
    /// canControllerScroll* re-sync to hover on every check), so the hovered
    /// cell IS the surface's focus truth — not the funnel's index. Note the
    /// hovered cell during the segment's own update pass and let the drain
    /// speak the item under it on change: one path serves physical-mouse
    /// hover and keyboard snaps alike. Name-and-amount composes from the
    /// same type-filtered list the segment renders and clicks from
    /// (UIInventorySheetBase.cs:113-148). Seam-gated (WP8).
    /// </summary>
    [HarmonyPatch]
    public static class InventoryHoverPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            if (Seams.InvSegment_update == null || Seams.InvSegment_grid == null
                || Seams.UIGridBase_getScrollableElementColumn == null
                || Seams.UIElement_getHover == null
                || Seams.UICanvas_getScrollableElements == null)
            {
                Plugin.Logger?.LogWarning("[InvHover] seams missing — inventory cells silent under the cursor");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.InvSegment_update;

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            try
            {
                object grid = Seams.InvSegment_grid.GetValue(__instance);
                if (grid == null) return;
                // The game's own hover-column read answers "no hover" in one
                // call — the common case costs almost nothing per frame.
                int col = (int)Seams.UIGridBase_getScrollableElementColumn.Invoke(grid, null);
                if (col < 0) return;
                var rows = Seams.UICanvas_getScrollableElements.Invoke(grid, null)
                    as System.Collections.IList;
                if (rows == null) return;
                for (int r = 0; r < rows.Count; r++)
                {
                    var cells = Seams.UICanvas_getScrollableElements.Invoke(rows[r], null)
                        as System.Collections.IList;
                    if (cells == null || col >= cells.Count) continue;
                    if ((bool)Seams.UIElement_getHover.Invoke(cells[col], null))
                    {
                        Pump.NoteInventoryHover(__instance, r, col);
                        return;
                    }
                }
            }
            catch { }
        }
    }
}

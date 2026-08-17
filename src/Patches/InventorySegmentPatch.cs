using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Character-inventory grid segments (owner ride finding 2026-08-17). The
    /// inventory sheet's grids are natively column-cursor navigable — the
    /// funnel walks one cell per ROW at the segment's current column — but two
    /// things were silent: cell landings composed to nothing (the icon cells
    /// carry no text; names live in the inventory's own type-filtered item
    /// list), and A/D column moves never write a selection index at all, so
    /// the join never heard them.
    ///
    ///  - Composition: Pump.ComposeInventoryCell maps (row, column, page
    ///    offset) onto the same filtered list the segment renders from —
    ///    Inventory.getListByType(itemTypes, includeEquipped: false), item
    ///    index = offset + row * width + column — and speaks name-and-amount
    ///    with the item's position among the segment's items.
    ///  - Column moves: prefix/postfix pair on the outer
    ///    GUIControl.controllerScrollSideways* diffs the segment's own column
    ///    field across the native call; a changed column notes a synthetic
    ///    selection (the drain's element-identity escape speaks the new cell)
    ///    and re-snaps the virtual mouse so the game's hover-driven vertical
    ///    walk stays live; an unchanged column speaks the edge, never silence.
    /// Seam-gated (WP8).
    /// </summary>
    public static class InventorySegmentPatch
    {
        private static bool Ready =>
            Seams.InventorySegmentType != null
            && Seams.GUIControl_getControllerScrollableList != null
            && Seams.InvSegment_column != null
            && Seams.GUIControl_setMouseToUIElement != null;

        [HarmonyPatch]
        public static class ColumnLeftHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready && Seams.GUIControl_controllerScrollSidewaysLeft != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_controllerScrollSidewaysLeft;

            [HarmonyPrefix]
            static void Prefix(object __instance, out object[] __state)
                => __state = Capture(__instance);

            [HarmonyPostfix]
            static void Postfix(object __instance, object[] __state)
                => Diff(__instance, __state, "First column.");
        }

        [HarmonyPatch]
        public static class ColumnRightHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready && Seams.GUIControl_controllerScrollSidewaysRight != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_controllerScrollSidewaysRight;

            [HarmonyPrefix]
            static void Prefix(object __instance, out object[] __state)
                => __state = Capture(__instance);

            [HarmonyPostfix]
            static void Postfix(object __instance, object[] __state)
                => Diff(__instance, __state, "Last column.");
        }

        /// <summary>(funnel list, segment, column) when the funnel currently
        /// walks a character-inventory surface; null on every other screen.
        /// The funnel list is the SHEET — its cells live on the controller
        /// focus surface (UIInventorySheetBase.cs:586-598), so the original
        /// segment-type check against the list itself never matched and this
        /// patch was dead from birth (owner ride 2026-08-17). Resolve through
        /// Pump.InventorySurfaceOf; a direct segment list still matches.</summary>
        private static object[] Capture(object gui)
        {
            try
            {
                object list = Seams.GUIControl_getControllerScrollableList.Invoke(gui, null);
                if (list == null) return null;
                object segment = Seams.InventorySegmentType.IsInstanceOfType(list)
                    ? list : Pump.InventorySurfaceOf(list);
                if (segment == null || !Seams.InventorySegmentType.IsInstanceOfType(segment)) return null;
                return new[] { list, segment, Seams.InvSegment_column.GetValue(segment) };
            }
            catch { return null; }
        }

        private static void Diff(object gui, object[] state, string edgeLine)
        {
            if (state == null) return;
            try
            {
                object list = state[0];
                object segment = state[1];
                int before = (int)state[2];
                // Re-read through the live chain — a press at the grid's last
                // column switches the sheet's focus surface (main ->
                // secondary, UIInventorySheetBase.cs:638-649) rather than
                // moving the column.
                object nowSegment = Seams.InventorySegmentType.IsInstanceOfType(list)
                    ? list : Pump.InventorySurfaceOf(list);
                if (nowSegment == null) return;
                int after = (int)Seams.InvSegment_column.GetValue(nowSegment);
                if (!ReferenceEquals(nowSegment, segment) || after != before)
                {
                    // Note the LIST the funnel walks (the sheet) — the drain
                    // composes the surface cell at the sheet's row, and its
                    // element-identity escape re-speaks same-row column moves
                    // (cells are stable ctor-created UIGridButtons). The
                    // native sideways call has already re-snapped the mouse.
                    Pump.NoteSelection(list);
                }
                else
                {
                    Scaffold.SpeechService.Say(edgeLine, "Nav");
                }
            }
            catch { }
        }
    }
}

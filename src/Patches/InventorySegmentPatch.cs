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

        /// <summary>(segment, column) when the funnel currently walks an
        /// inventory segment; null on every other screen.</summary>
        private static object[] Capture(object gui)
        {
            try
            {
                object list = Seams.GUIControl_getControllerScrollableList.Invoke(gui, null);
                if (list == null || !Seams.InventorySegmentType.IsInstanceOfType(list)) return null;
                return new[] { list, Seams.InvSegment_column.GetValue(list) };
            }
            catch { return null; }
        }

        private static void Diff(object gui, object[] state, string edgeLine)
        {
            if (state == null) return;
            try
            {
                object segment = state[0];
                int before = (int)state[1];
                int after = (int)Seams.InvSegment_column.GetValue(segment);
                if (after != before)
                {
                    // The drain's element-identity escape turns this into the
                    // new cell's line; the snap keeps the game's hover-driven
                    // vertical walk anchored on the new column.
                    Pump.NoteSelection(segment);
                    Seams.GUIControl_setMouseToUIElement.Invoke(gui, new object[] { segment, 2, -6 });
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

using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Worn-equipment zone (owner build 2026-08-17). The Items Worn grid is a
    /// mouse island: equipped items are excluded from the main grid
    /// (includeEquipped: false) and the sheet's controller rotation only ever
    /// toggles main↔secondary (UIInventorySheetBase.cs:638-649) — unequip was
    /// keyboard-impossible. The zone joins the A/D chain (the 45acaca sheet-zone
    /// precedent): A at the main grid's first column crosses in at the worn
    /// grid's last column, A/D walk its columns, D past the last column crosses
    /// back — every crossing reversible.
    ///
    /// The promotion rides the game's own routing: while active, the sheet's
    /// currentControllerSurface IS the worn segment (no proxy flag — the game's
    /// surface field is the truth), which makes the main grid's scrollbar
    /// side-channels no-ops and puts InventorySegmentPatch dormant for free.
    /// Two mod assists remain: the sheet's scrollable list swaps to the worn
    /// grid's column cells (ItemsWornUI's native list is its canvas children,
    /// not cells), and A/D presses are claimed while inside (the native
    /// sideways would instantly bounce the surface back to main). W/S then
    /// walks the two worn rows through the untouched native funnel, the mouse
    /// snaps onto real cells, and Z/X run the game's own click handlers —
    /// select, second-Z unequip toggle, right-click tooltip
    /// (UIGridBase.cs:104-143). Seam-gated (WP8).
    /// </summary>
    public static class WornZonePatch
    {
        private static object _activeSheet;
        internal static int Column;            // worn column the zone sits on
        internal static object WornCharacter;  // last object ItemsWornUI rendered

        internal static bool IsActiveFor(object sheet)
            => sheet != null && ReferenceEquals(sheet, _activeSheet);

        private static bool Ready =>
            Seams.ItemsWornUIType != null
            && Seams.InvSheet_itemInteractionGrid != null
            && Seams.InvSheet_currentControllerSurface != null
            && Seams.InvSheet_mainInventoryGrid != null
            && Seams.ItemsWornUI_grid != null
            && Seams.UIGridBase_getScrollableElementsAtColumn != null
            && Seams.UIInvSheet_getScrollableElements != null
            && Seams.GUIControl_getControllerScrollableList != null
            && Seams.GUIControl_setMouseToUIElement != null
            && Seams.UIInventorySheetBaseType != null;

        /// <summary>Entry, called from the segment patch's left-edge branch.
        /// True when this sheet has a worn grid and the zone claimed the press.</summary>
        internal static bool TryEnter(object gui, object sheet)
        {
            if (!Ready) return false;
            // Gate D: the table owns the worn zone as a section on its
            // registered states — this patch stands down whole there (the
            // SheetGridZonePatch precedent). With A/D swallowed its entry
            // gesture cannot fire; the guard covers the residue.
            if (TableCursor.OwnsCurrentState()) return false;
            try
            {
                object worn = Seams.InvSheet_itemInteractionGrid.GetValue(sheet);
                if (worn == null || !Seams.ItemsWornUIType.IsInstanceOfType(worn)) return false;
                _activeSheet = sheet;
                Column = WornWidth(worn) - 1;  // chain-adjacent: entered from
                                               // the main grid's first column
                Seams.InvSheet_currentControllerSurface.SetValue(sheet, worn);
                Snap(gui, sheet);
                Pump.NoteWornZoneEntry(sheet);
                return true;
            }
            catch { _activeSheet = null; return false; }
        }

        private static void Exit(object gui, object sheet)
        {
            _activeSheet = null;
            try
            {
                Seams.InvSheet_currentControllerSurface.SetValue(sheet,
                    Seams.InvSheet_mainInventoryGrid.GetValue(sheet));
                Snap(gui, sheet);
            }
            catch { }
            Pump.NoteWornZoneExit(sheet);
        }

        /// <summary>State transitions drop the zone with the screen.</summary>
        internal static void OnStateTransition() => _activeSheet = null;

        private static int WornWidth(object worn)
        {
            try
            {
                object grid = Seams.ItemsWornUI_grid.GetValue(worn);
                if (grid != null && Seams.UIGridBase_width != null)
                    return (int)Seams.UIGridBase_width.GetValue(grid);
            }
            catch { }
            return 6;  // WornGridUI ctor bound (UIInventorySheetBase.cs:228)
        }

        /// <summary>The game's own snap, aimed at the sheet: its current
        /// selected element resolves through our swapped list to the worn cell,
        /// clamping the row on read exactly as the game does. Same offsets as
        /// GUIControlInventoryBase.setMouseToSelectedOption.</summary>
        private static void Snap(object gui, object sheet)
        {
            try { Seams.GUIControl_setMouseToUIElement.Invoke(gui, new object[] { sheet, 8, -8 }); }
            catch { }
        }

        /// <summary>While active, the sheet's scrollable list is the worn
        /// grid's cell-per-row column — the native funnel walks it unchanged.</summary>
        [HarmonyPatch]
        public static class WornSurfaceList
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.UIInvSheet_getScrollableElements;

            [HarmonyPostfix]
            static void Postfix(object __instance, ref System.Collections.Generic.List<UIElement> __result)
            {
                if (!IsActiveFor(__instance)) return;
                if (TableCursor.OwnsCurrentState()) return; // gate-D stand-down
                try
                {
                    object worn = Seams.InvSheet_itemInteractionGrid.GetValue(__instance);
                    object grid = worn == null ? null : Seams.ItemsWornUI_grid.GetValue(worn);
                    if (grid == null) return;
                    var cells = Seams.UIGridBase_getScrollableElementsAtColumn
                        .Invoke(grid, new object[] { Column })
                        as System.Collections.Generic.List<UIElement>;
                    if (cells != null && cells.Count > 0) __result = cells;
                }
                catch { }
            }
        }

        /// <summary>The renderer's own update carries the character whose worn
        /// slots it paints — the compose reads the same getters, same source.</summary>
        [HarmonyPatch]
        public static class WornRenderNote
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready && Seams.ItemsWornUI_update != null && Seams.CharacterType != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.ItemsWornUI_update;

            [HarmonyPostfix]
            static void Postfix(object obj)
            {
                if (obj != null && Seams.CharacterType.IsInstanceOfType(obj)) WornCharacter = obj;
            }
        }

        [HarmonyPatch]
        public static class SidewaysLeftClaim
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready && Seams.GUIControl_controllerScrollSidewaysLeft != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_controllerScrollSidewaysLeft;

            [HarmonyPrefix]
            static bool Prefix(object __instance) => Sideways(__instance, -1);
        }

        [HarmonyPatch]
        public static class SidewaysRightClaim
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready && Seams.GUIControl_controllerScrollSidewaysRight != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_controllerScrollSidewaysRight;

            [HarmonyPrefix]
            static bool Prefix(object __instance) => Sideways(__instance, +1);
        }

        /// <summary>False = the zone consumed the press (skip the native
        /// sideways — it would bounce the surface straight back to main).
        /// The InventorySegmentPatch pair on these same methods stays dormant
        /// while active: its surface resolve sees the worn segment, which is
        /// not a UIGridCharacterInventorySegment.</summary>
        private static bool Sideways(object gui, int dir)
        {
            if (_activeSheet == null) return true;
            if (TableCursor.OwnsCurrentState()) return true; // gate-D stand-down
            try
            {
                object list = Seams.GUIControl_getControllerScrollableList.Invoke(gui, null);
                if (!IsActiveFor(list)) return true;  // another control's sideways
                int next = Column + dir;
                if (next < 0)
                {
                    Scaffold.SpeechService.Say("First column.", "Nav");
                    return false;
                }
                object worn = Seams.InvSheet_itemInteractionGrid.GetValue(list);
                if (next >= WornWidth(worn))
                {
                    Exit(gui, list);
                    return false;
                }
                Column = next;
                Snap(gui, list);
                Pump.NoteSelection(list);
                return false;
            }
            catch { return true; }
        }
    }
}

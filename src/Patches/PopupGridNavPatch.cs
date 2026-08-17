using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Loot-popup grid navigation (owner-approved 2026-08-17). The loot popup's
    /// item grid was unreachable by stick: PopUpUIBase only ever walks its
    /// "controller scrollable canvas", which defaults to the button row, and —
    /// unlike the spell-selector popup (PopUpBase.cs:633-671, the shipped
    /// sibling this patch copies verbatim) — PopUpUISystemInventory never
    /// rotates that canvas into its grid. Two wires, both port-first:
    ///
    ///  1. Rotation: on the popup's own setMouseToClosestButtonAbove/Below,
    ///     when the active canvas can't scroll further, hand it the grid
    ///     (up) or the button row (down) — the spell selector's exact logic.
    ///     The crossing is voiced by the existing CanvasSwitchPatch join.
    ///  2. Cell flattening: UIGridInventory inherits the generic scrollable
    ///     list, which returns its two ROWS; the ability-grid family flattens
    ///     rows into cells (UIAbilitySelectorGrid.cs:142-157). Supply that
    ///     missing override via postfix, bounded to the real item count so
    ///     arrows never land on empty slots. Grid order = inventory order,
    ///     so W/S steps item by item.
    ///
    /// Everything downstream is native: the snap keeps hover true, the game's
    /// own handle() narrates the hovered item into the tertiary line, Z = LT
    /// = left click loots the focused item, X = RT = right click raises the
    /// compare tooltip. Composition of the focused cell's name + count lives
    /// in Pump.ComposeSelection (reads the popup's own inventory list,
    /// read-only). Seam-gated (WP8).
    /// </summary>
    public static class PopupGridNavPatch
    {
        // ---- (1) Canvas rotation ----

        private static bool RotationReady =>
            Seams.PopUpUISystemInventoryType != null
            && Seams.PopUpUI_grid != null
            && Seams.PopUpUIBase_buttons != null
            && Seams.PopUpUIBase_getControllerScrollableUICanvas != null
            && Seams.PopUpUIBase_setControllerScrollableUICanvas != null
            && Seams.PopUpUIBase_setMouseToSelectedButton != null
            && Seams.UICanvas_canControllerScrollUp != null
            && Seams.UICanvas_canControllerScrollDown != null
            && Seams.UICanvas_incrementCurrentSelectedButton != null
            && Seams.UICanvas_decrementCurrentSelectedButton != null;

        [HarmonyPatch]
        public static class LootRotateAboveHook
        {
            [HarmonyPrepare]
            static bool Prepare()
            {
                if (Seams.PopUpUIBase_setMouseToClosestButtonAbove == null || !RotationReady)
                {
                    Plugin.Logger?.LogWarning("[LootGrid] rotation seams missing — loot grid stays button-only");
                    return false;
                }
                return true;
            }

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.PopUpUIBase_setMouseToClosestButtonAbove;

            [HarmonyPrefix]
            static bool Prefix(object __instance) => !Rotate(__instance, up: true);
        }

        [HarmonyPatch]
        public static class LootRotateBelowHook
        {
            [HarmonyPrepare]
            static bool Prepare() =>
                Seams.PopUpUIBase_setMouseToClosestButtonBelow != null && RotationReady;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.PopUpUIBase_setMouseToClosestButtonBelow;

            [HarmonyPrefix]
            static bool Prefix(object __instance) => !Rotate(__instance, up: false);
        }

        /// <summary>The spell selector's rotation, replayed on the loot popup's
        /// UI. Returns true when handled (native call skipped).</summary>
        private static bool Rotate(object uiElements, bool up)
        {
            try
            {
                if (uiElements == null || !Seams.PopUpUISystemInventoryType.IsInstanceOfType(uiElements))
                    return false; // every other popup keeps its native behavior

                object canvas = Seams.PopUpUIBase_getControllerScrollableUICanvas.Invoke(uiElements, null);
                if (canvas == null) return false;

                if (up)
                {
                    if ((bool)Seams.UICanvas_canControllerScrollUp.Invoke(canvas, null))
                        Seams.UICanvas_decrementCurrentSelectedButton.Invoke(canvas, null);
                    else
                        Seams.PopUpUIBase_setControllerScrollableUICanvas.Invoke(uiElements,
                            new[] { Seams.PopUpUI_grid.GetValue(uiElements) });
                }
                else
                {
                    if ((bool)Seams.UICanvas_canControllerScrollDown.Invoke(canvas, null))
                        Seams.UICanvas_incrementCurrentSelectedButton.Invoke(canvas, null);
                    else
                        Seams.PopUpUIBase_setControllerScrollableUICanvas.Invoke(uiElements,
                            new[] { Seams.PopUpUIBase_buttons.GetValue(uiElements) });
                }
                Seams.PopUpUIBase_setMouseToSelectedButton.Invoke(uiElements, null);
                return true;
            }
            catch { return false; }
        }

        // ---- Tooltip discipline (WP11 class, popup edition — owner ride
        //      2026-08-17): a raised tooltip spawns AT the virtual mouse,
        //      steals hover from the cell (the highlight visibly "shunts"
        //      away), and gates the popup's press handling behind
        //      isMouseOverTooltip. Any stick move clears it first, restoring
        //      cell hover before the move runs. ----

        [HarmonyPatch]
        public static class TooltipClearOnMoveHook
        {
            [HarmonyPrepare]
            static bool Prepare() =>
                Seams.PopUpBase_updateControllerScrolling != null
                && Seams.ToolTipPrinter_hasToolTip != null
                && Seams.ToolTipPrinter_clearToolTip != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.PopUpBase_updateControllerScrolling;

            [HarmonyPrefix]
            static void Prefix()
            {
                try
                {
                    if (!(bool)Seams.ToolTipPrinter_hasToolTip.Invoke(null, null)) return;
                    if (Pressed(Seams.SkaldIO_getOptionSelectionButtonUp)
                        || Pressed(Seams.SkaldIO_getOptionSelectionButtonDown)
                        || Pressed(Seams.SkaldIO_getOptionSelectionButtonLeft)
                        || Pressed(Seams.SkaldIO_getOptionSelectionButtonRight))
                        Seams.ToolTipPrinter_clearToolTip.Invoke(null, null);
                }
                catch { }
            }

            private static bool Pressed(MethodInfo accessor)
                => accessor != null && (bool)accessor.Invoke(null, null);
        }

        // ---- (2) Cell flattening ----

        [HarmonyPatch]
        public static class LootGridFlattenHook
        {
            [HarmonyPrepare]
            static bool Prepare() =>
                Seams.UIGridInventoryType != null
                && Seams.UICanvas_getScrollableElements != null
                && Seams.UIButtonControlBase_getButtonsList != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.UICanvas_getScrollableElements;

            [HarmonyPostfix]
            static void Postfix(object __instance, ref List<UIElement> __result)
            {
                if (!Seams.UIGridInventoryType.IsInstanceOfType(__instance)) return;
                try
                {
                    var cells = new List<UIElement>();
                    foreach (object row in __result)
                    {
                        var rowButtons = Seams.UIButtonControlBase_getButtonsList
                            .Invoke(row, null) as System.Collections.IList;
                        if (rowButtons == null) continue;
                        foreach (object cell in rowButtons)
                            if (cell is UIElement el) cells.Add(el);
                    }
                    // Bound to the real item count so arrows never land empty
                    // slots (the game's hover echo would speak a stale name).
                    int count = LootItemCount();
                    if (count >= 0 && count < cells.Count)
                        cells.RemoveRange(count, cells.Count - count);
                    if (cells.Count > 0) __result = cells;
                }
                catch { /* keep the native row list */ }
            }
        }

        /// <summary>Item count of the loot popup currently on top of the
        /// stack; -1 when unresolvable (leave the cell list unbounded).</summary>
        private static int LootItemCount()
        {
            try
            {
                var list = CurrentLootItemList();
                return list?.Count ?? -1;
            }
            catch { return -1; }
        }

        /// <summary>The current loot popup's inventory items, via the game's
        /// read-only list accessor (never getObjectByIndex — that write-mutates
        /// the current object). Null when no loot popup is up.</summary>
        internal static System.Collections.IList CurrentLootItemList()
        {
            try
            {
                if (Seams.PopUpControl_getCurrentPopUp == null || Seams.PopUpLootType == null
                    || Seams.PopUpLoot_inventory == null || Seams.SkaldObjectList_getObjectList == null)
                    return null;
                object popup = Seams.PopUpControl_getCurrentPopUp.Invoke(null, null);
                if (popup == null || !Seams.PopUpLootType.IsInstanceOfType(popup)) return null;
                object inventory = Seams.PopUpLoot_inventory.GetValue(popup);
                if (inventory == null) return null;
                return Seams.SkaldObjectList_getObjectList.Invoke(inventory, null) as System.Collections.IList;
            }
            catch { return null; }
        }
    }
}

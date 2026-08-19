using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Crafting zone chain (owner build 2026-08-18). The crafting sheet's
    /// native controller chain is recipes ↔ party grid only
    /// (UIInventorySheetCrafting.cs:78-86) — the 3×2 workstation grid and the
    /// Craft/Clear technical row are mouse islands. Craft's only press paths
    /// in the shipped game are a mouse click or controller face-button A
    /// (AXBY-no-numbers scheme; digits deliberately excluded), and the mod
    /// feeds no A button — crafting was uncompletable by keyboard.
    ///
    /// The chain becomes: recipes ↔ workstation ↔ Craft/Clear ↔ party grid
    /// (A/D crossings, every one reversible; owner ruling: all three added
    /// sections labeled). Zone truth is the game's own
    /// currentControllerSurface field (no proxy — the worn-zone precedent):
    /// pointing it at the bench segment or the buttons row makes everything
    /// downstream native — the sheet's getScrollableElements delegates to the
    /// surface, the funnel walks bench rows / the two buttons, the mouse
    /// snaps onto real elements, cells compose from the bench segment's OWN
    /// inventory through the existing inventory-cell composer, and Z is the
    /// native click (bench second-Z returns the ingredient to the party;
    /// Craft click starts the craft). The only mod work is the crossings —
    /// claimed at the GUIControl sideways choke — and their labels (the
    /// game's own rendered TextLables: "Recipes"/"Workstation"/"Party
    /// Inventory"; the buttons zone self-labels by landing on Craft/Clear,
    /// whose rendered text the selection join speaks).
    ///
    /// Coordination: bench and party are real inventory segments, so a
    /// claimed edge press would otherwise also trip InventorySegmentPatch's
    /// unmoved-column edge line ("First column.") over the crossing — the
    /// claim stamps the frame and the segment patch yields (same shape as its
    /// worn-zone insertion). Seam-gated (WP8).
    /// </summary>
    public static class CraftingZonePatch
    {
        private static bool Ready =>
            Seams.UIInventorySheetCraftingType != null
            && Seams.InvSheetCrafting_listButtons != null
            && Seams.InvSheetCrafting_buttons != null
            && Seams.InvSheet_secondaryInventoryGrid != null
            && Seams.InvSheet_currentControllerSurface != null
            && Seams.InvSheet_mainInventoryGrid != null
            && Seams.InvSegment_column != null
            && Seams.InvSegment_gridWidth != null
            && Seams.GUIControl_getControllerScrollableList != null
            && Seams.GUIControl_setMouseToUIElement != null
            && Seams.UICanvas_setCurrentSelectedButton != null
            && Seams.UICanvas_getCurrentSelectedButtonIndex != null
            && Seams.UICanvas_getScrollableElements != null;

        private static int _claimFrame = -1;

        /// <summary>True when a crossing claimed this frame — consulted by
        /// InventorySegmentPatch so its edge line never talks over a
        /// crossing announcement.</summary>
        internal static bool ClaimedThisFrame
            => UnityEngine.Time.frameCount == _claimFrame;

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
        /// sideways — it would jump recipes↔party straight over the new
        /// zones). True = native behavior (all non-crafting screens, and
        /// internal bench/party column moves, which native + the segment
        /// patch already handle).</summary>
        private static bool Sideways(object gui, int dir)
        {
            if (!Ready) return true;
            try
            {
                object sheet = Seams.GUIControl_getControllerScrollableList.Invoke(gui, null);
                if (sheet == null || !Seams.UIInventorySheetCraftingType.IsInstanceOfType(sheet))
                    return true;

                object recipes = Seams.InvSheetCrafting_listButtons.GetValue(sheet);
                object bench = Seams.InvSheet_secondaryInventoryGrid.GetValue(sheet);
                object buttons = Seams.InvSheetCrafting_buttons.GetValue(sheet);
                object party = Seams.InvSheet_mainInventoryGrid.GetValue(sheet);
                object surface = Seams.InvSheet_currentControllerSurface.GetValue(sheet) ?? party;
                if (recipes == null || bench == null || buttons == null || party == null)
                    return true;

                // Recipes → D → workstation (native would jump to party).
                if (ReferenceEquals(surface, recipes))
                {
                    if (dir <= 0) return true; // A on recipes: native (nothing left)
                    EnterBench(gui, sheet, bench, column: 0);
                    return false;
                }

                if (ReferenceEquals(surface, bench))
                {
                    int col = (int)Seams.InvSegment_column.GetValue(bench);
                    int width = BenchWidth(bench);
                    if (dir < 0 && col <= 0)
                    {
                        // Workstation → A → recipes. The label rides the
                        // landing line (one utterance — the ride's same-frame
                        // interrupt pairs, Opus log review 2026-08-19).
                        SetSurface(sheet, recipes);
                        Claim();
                        Snap(gui, sheet);
                        Pump.NoteZoneLabel("Recipes");
                        Pump.NoteSelection(sheet);
                        return false;
                    }
                    if (dir > 0 && col >= width - 1)
                    {
                        // Workstation → D → Craft/Clear, landing on Craft.
                        EnterButtons(gui, sheet, buttons, index: 0);
                        return false;
                    }
                    return true; // internal column move — native + segment patch
                }

                if (ReferenceEquals(surface, buttons))
                {
                    int idx = CurrentIndex(sheet);
                    int count = ButtonCount(buttons);
                    if (dir < 0)
                    {
                        if (idx <= 0)
                        {
                            // Craft → A → workstation's last column (chain-adjacent).
                            EnterBench(gui, sheet, bench, column: BenchWidth(bench) - 1);
                        }
                        else MoveIndex(gui, sheet, idx - 1);
                        return false;
                    }
                    if (idx >= count - 1)
                    {
                        // Clear → D → party grid.
                        SetSurface(sheet, party);
                        Claim();
                        SetIndex(sheet, 0);
                        Snap(gui, sheet);
                        Pump.NoteZoneLabel("Party Inventory");
                        Pump.NoteSelection(sheet);
                    }
                    else MoveIndex(gui, sheet, idx + 1);
                    return false;
                }

                if (ReferenceEquals(surface, party) && dir < 0)
                {
                    int col = (int)Seams.InvSegment_column.GetValue(party);
                    if (col <= 0)
                    {
                        // Party grid → A → Craft/Clear, landing on Clear (near edge).
                        EnterButtons(gui, sheet, buttons, index: ButtonCount(buttons) - 1);
                        return false;
                    }
                    return true; // internal column move
                }
            }
            catch { }
            return true;
        }

        private static void EnterBench(object gui, object sheet, object bench, int column)
        {
            SetSurface(sheet, bench);
            Claim();
            try { Seams.InvSegment_column.SetValue(bench, column); } catch { }
            SetIndex(sheet, 0); // top bench row — deterministic landing
            Snap(gui, sheet);
            Pump.NoteZoneLabel("Workstation"); // rides the landed cell's line
            Pump.NoteSelection(sheet);         // the drain composes the landed cell
        }

        private static void EnterButtons(object gui, object sheet, object buttons, int index)
        {
            SetSurface(sheet, buttons);
            Claim();
            SetIndex(sheet, index); // the setter is the patched selection
            Snap(gui, sheet);       // join's write point — the landed button's
                                    // rendered text ("Craft"/"Clear") speaks
                                    // through the normal focus path
            Pump.NoteSelection(sheet);
        }

        private static void MoveIndex(object gui, object sheet, int index)
        {
            Claim();
            SetIndex(sheet, index);
            Snap(gui, sheet);
            Pump.NoteSelection(sheet);
        }

        private static void Claim() => _claimFrame = UnityEngine.Time.frameCount;

        private static void SetSurface(object sheet, object surface)
        {
            try { Seams.InvSheet_currentControllerSurface.SetValue(sheet, surface); } catch { }
        }

        private static void SetIndex(object sheet, int index)
        {
            try { Seams.UICanvas_setCurrentSelectedButton.Invoke(sheet, new object[] { index }); }
            catch { }
        }

        private static int CurrentIndex(object sheet)
        {
            try
            {
                object idx = Seams.UICanvas_getCurrentSelectedButtonIndex.Invoke(sheet, null);
                return idx is int i ? i : 0;
            }
            catch { return 0; }
        }

        private static int BenchWidth(object bench)
        {
            try { return (int)Seams.InvSegment_gridWidth.GetValue(bench); }
            catch { return 3; } // UIInventorySheetCrafting ctor bound (3, 2)
        }

        private static int ButtonCount(object buttons)
        {
            try
            {
                var elements = Seams.UICanvas_getScrollableElements.Invoke(buttons, null)
                    as System.Collections.IList;
                if (elements != null && elements.Count > 0) return elements.Count;
            }
            catch { }
            return 2; // "Craft", "Clear" (UIInventorySheetCrafting.cs:63)
        }

        /// <summary>The mouse snap through the game's own helper, aimed at the
        /// sheet: its selected element resolves through the delegated surface
        /// (bench cell / technical button), same offsets as the sheet's own
        /// setMouseToSelectedOption idiom.</summary>
        private static void Snap(object gui, object sheet)
        {
            try { Seams.GUIControl_setMouseToUIElement.Invoke(gui, new object[] { sheet, 8, -8 }); }
            catch { }
        }
    }
}

using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Attribute-editor flip join (owner rulings 2026-08-17) + flip resync
    /// (B15, owner rulings 2026-08-30 off the ninetails16 log).
    ///
    /// FLIP JOIN: A/D on the CC stats / attribute editor rows moves the row
    /// cursor between the plus and minus arrow columns
    /// (EditorSheetEntry.controllerScrollToPlusButton — the settings-slider
    /// idiom, image-arrow edition) and was entirely silent. Speak the side —
    /// "Plus." / "Minus." Note-only postfix at the sheet level: one call per
    /// press covers both entries (the sheet forwards the flip to attributes
    /// and skills together); the drain reads the game's own flag at the
    /// clock, no proxy determiners.
    ///
    /// FLIP RESYNC (B8 first half): the flip's snap targets
    /// scrollableElements[currentSelectedButton], but the canvas index tracks
    /// hover only through up/down presses (UICanvas.increment/decrement walk
    /// for the hovered element; they silently no-op when nothing scrollable
    /// is hovered, and the index starts at -1 → bounds to 0). A flip pressed
    /// before any up/down — canonically right after the screen's intro popup
    /// parked the mouse — teleported the cursor to row zero (ninetails16 log
    /// f9022: Willpower → Agility on a single Left). The prefix calls the
    /// game's own setCurrentSelectedButtonIndexToHoveredElement on the sheet
    /// canvas BEFORE the flag flips (the hovered column is still the
    /// scrollable list), so the snap lands on the same row in the other
    /// column. No hover → the game's own fallback stands, and the landing
    /// join below makes it audible.
    ///
    /// ROW LANDINGS — RETIRED 2026-09-02 (Shane's 0.5.5 log): the hover-
    /// diffed landing join (updateEntry1/2 postfixes → Pump.DrainEditorRow)
    /// spoke every row a SECOND time two frames after the cursor's own index
    /// landing (TableCursor.ComposeSheetCell already composes the identical
    /// "{name}, {side}, {i} of {n}" line at the press), and its gap re-arm
    /// re-spoke the row after every plus/minus click. With the funnel now an
    /// index walk (SkaldIOPatches.FunnelStep) hover never leads the cursor,
    /// so a hover-only landing has nothing left to announce. The flip join
    /// and resync above stand. Seam-gated (WP8).
    /// </summary>
    [HarmonyPatch]
    public static class EditorFlipLeftPatch
    {
        [HarmonyPrepare]
        static bool Prepare() => Seams.AttributeEditor_scrollSidewaysLeft != null
            && Seams.CharacterSheet_entry1 != null && Seams.EditorEntry_scrollToPlusButton != null;

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.AttributeEditor_scrollSidewaysLeft;

        [HarmonyPrefix]
        static void Prefix(object __instance) => EditorFlipResync.Apply(__instance);

        [HarmonyPostfix]
        static void Postfix(object __instance) => Pump.NoteEditorFlip(__instance);
    }

    [HarmonyPatch]
    public static class EditorFlipRightPatch
    {
        [HarmonyPrepare]
        static bool Prepare() => Seams.AttributeEditor_scrollSidewaysRight != null
            && Seams.CharacterSheet_entry1 != null && Seams.EditorEntry_scrollToPlusButton != null;

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.AttributeEditor_scrollSidewaysRight;

        [HarmonyPrefix]
        static void Prefix(object __instance) => EditorFlipResync.Apply(__instance);

        [HarmonyPostfix]
        static void Postfix(object __instance) => Pump.NoteEditorFlip(__instance);
    }

    internal static class EditorFlipResync
    {
        /// <summary>Align the sheet canvas's remembered index to the hovered
        /// row before the flag flips — the game's own sync method, on the
        /// game's own canvas (SheetComplexDoubleColumn.getControllerScrollableList
        /// returns the sheet itself). A missing seam or no hover leaves the
        /// native behavior untouched.</summary>
        internal static void Apply(object sheet)
        {
            try { Seams.UICanvas_syncSelectedIndexToHover?.Invoke(sheet, null); }
            catch (System.Exception ex) { Plugin.Logger?.LogDebug($"[EditorFlip:resync] {ex.Message}"); }
        }
    }
}

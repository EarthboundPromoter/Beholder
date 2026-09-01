using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Attribute-editor flip join (owner rulings 2026-08-17) + flip resync +
    /// row landings (B8, owner rulings 2026-08-30 off the ninetails16 log).
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
    /// ROW LANDINGS (B8 second half): rows on this sheet landed with no
    /// label — identity arrived only as the queued full description. The
    /// updateEntry1/2 postfixes note the hovered row each frame (the sheet's
    /// own per-frame update, latest wins); the drain diffs by row name and
    /// speaks "{name}, {side}, {i} of {n}" — the slider-row idiom
    /// (Pump.DrainEditorRow). A same-frame flip is consumed by the landing
    /// line, which already carries the side. Seam-gated (WP8).
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

    // ---- Row landings: note the hovered row from the sheet's own entry
    //      updates (they run every frame from the state's setGUIData) ----

    [HarmonyPatch]
    public static class EditorRowLandingAttributesPatch
    {
        [HarmonyPrepare]
        static bool Prepare() => Seams.AttrSheet_updateEntry1 != null
            && Seams.CharacterSheet_entry1 != null && Seams.SheetEntry_getCurrentObject != null;

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.AttrSheet_updateEntry1;

        [HarmonyPostfix]
        static void Postfix(object __instance, object __0)
            => EditorRowLanding.Note(__instance, Seams.CharacterSheet_entry1, __0);
    }

    [HarmonyPatch]
    public static class EditorRowLandingSkillsPatch
    {
        [HarmonyPrepare]
        static bool Prepare() => Seams.AttrSheet_updateEntry2 != null
            && Seams.CharacterSheet_entry2 != null && Seams.SheetEntry_getCurrentObject != null;

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.AttrSheet_updateEntry2;

        [HarmonyPostfix]
        static void Postfix(object __instance, object __0)
            => EditorRowLanding.Note(__instance, Seams.CharacterSheet_entry2, __0);
    }

    internal static class EditorRowLanding
    {
        /// <summary>Note this entry's hovered row (null on no-hover frames)
        /// plus its position among the data rows — slot 0 of the game's list
        /// is the HEADER row (EditorSheetEntry.update reads hoverIndex + 1),
        /// so list index 1 is spoken position 1. Reference-find is safe: the
        /// entry's currentObject was assigned from this same list this frame.</summary>
        internal static void Note(object sheet, FieldInfo entryField, object data)
        {
            try
            {
                object entry = entryField.GetValue(sheet);
                if (entry == null) return;
                object row = Seams.SheetEntry_getCurrentObject.Invoke(entry, null);
                int index = -1, count = -1;
                if (row != null && data != null && Seams.SkaldDataList_getObjectList != null)
                {
                    if (Seams.SkaldDataList_getObjectList.Invoke(data, null) is System.Collections.IList list)
                    {
                        count = list.Count - 1;
                        for (int i = 1; i < list.Count; i++)
                            if (ReferenceEquals(list[i], row)) { index = i; break; }
                    }
                }
                Pump.NoteEditorRow(sheet, row, index, count);
            }
            catch (System.Exception ex)
            {
                Scaffold.Log.Throttled("EditorRow:note", ex.Message);
            }
        }
    }
}

using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Attribute-editor flip join (owner rulings 2026-08-17). A/D on the CC
    /// stats / attribute editor rows moves the row cursor between the plus and
    /// minus arrow columns (EditorSheetEntry.controllerScrollToPlusButton —
    /// the settings-slider idiom, image-arrow edition) and was entirely
    /// silent. Speak ONLY the side — "Plus." / "Minus." — never the row body.
    /// Note-only postfix at the sheet level: one call per press covers both
    /// entries (the sheet forwards the flip to attributes and skills
    /// together); the drain reads the game's own flag at the clock, no proxy
    /// determiners. Seam-gated (WP8).
    /// </summary>
    [HarmonyPatch]
    public static class EditorFlipLeftPatch
    {
        [HarmonyPrepare]
        static bool Prepare() => Seams.AttributeEditor_scrollSidewaysLeft != null
            && Seams.CharacterSheet_entry1 != null && Seams.EditorEntry_scrollToPlusButton != null;

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.AttributeEditor_scrollSidewaysLeft;

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

        [HarmonyPostfix]
        static void Postfix(object __instance) => Pump.NoteEditorFlip(__instance);
    }
}

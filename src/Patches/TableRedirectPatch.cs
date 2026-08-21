using HarmonyLib;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// The table engine's section redirect (table-ui gate A): while the
    /// Buttons section is current on a registered table screen, the game's
    /// own getControllerScrollableList answers with the numeric row — so the
    /// funnel calls, the edge observer, and the focus-routing truth
    /// (FocusRestsOnNumericRow) all follow the section, natively.
    ///
    /// Target is SheetClass.getControllerScrollableList: every gate-B
    /// multi-section screen runs a GUIControlSheet, and GUIControlSheet does
    /// not re-override (a Harmony patch on the GUIControl base would never
    /// see these calls — virtual dispatch lands on the SheetClass override).
    /// Coexists with SheetGridZonePatch.ZoneWireHook on the same method:
    /// the two are state-disjoint (grimoire/abilities vs the menu/journal
    /// family), so at most one ever writes the result.
    ///
    /// Applied in the deferred SkaldIO batch (game-type detour).
    /// </summary>
    public static class TableRedirectPatch
    {
        public static void Apply(Harmony harmony)
        {
            if (Seams.SheetClass_getControllerScrollableList == null)
            {
                Plugin.Logger?.LogError("[Table] SheetClass.getControllerScrollableList seam missing — section redirect unavailable (Buttons sections degrade to numbers-only)");
                return;
            }
            harmony.Patch(Seams.SheetClass_getControllerScrollableList,
                prefix: new HarmonyMethod(typeof(TableRedirectPatch), nameof(Prefix_ScrollableList)));
            Plugin.Logger?.LogInfo("[Table] Section redirect armed on SheetClass.getControllerScrollableList");
        }

        static bool Prefix_ScrollableList(object __instance, ref object __result)
        {
            object over = TableCursor.ScrollableListOverride(__instance);
            if (over == null) return true;
            __result = over;
            return false;
        }
    }
}

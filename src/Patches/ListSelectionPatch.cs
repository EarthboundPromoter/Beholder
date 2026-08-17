using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// The list-selection join (owner ride finding, 2026-08-16 — ledger B6).
    ///
    /// Every list sheet has TWO cursors: the funnel focus (what stick/W-S moves,
    /// what selection speech reads) and the SkaldObjectList current object (what
    /// a CLICK sets, what the game highlights in yellow, and what actions —
    /// rebinding, class choice, loading a save, crafting — actually act on).
    /// Arrowing never selects; clicking does. The game shows the divergence as
    /// the yellow row; this join makes it audible.
    ///
    /// Write seams, note-only: setCurrentObject (direct writers) and
    /// getObjectByIndex (the click path — a read that MUTATES the selection;
    /// every state's list-click handler routes through it via
    /// getObjectByPageIndex). The drain reads getCurrentObject at the clock,
    /// diffs against the last spoken (list, object) pair, and speaks
    /// "Selected: &lt;row&gt;." — repeat notes and read-noise collapse in the diff.
    /// Seam-gated (WP8).
    /// </summary>
    [HarmonyPatch]
    public static class ListSelectionPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            if (Seams.SkaldObjectList_setCurrentObject == null
                && Seams.SkaldObjectList_getObjectByIndex == null)
            {
                Plugin.Logger?.LogError("[ListSel] SkaldObjectList write seams missing — selection state unvoiced");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> TargetMethods()
        {
            if (Seams.SkaldObjectList_setCurrentObject != null)
                yield return Seams.SkaldObjectList_setCurrentObject;
            if (Seams.SkaldObjectList_getObjectByIndex != null)
                yield return Seams.SkaldObjectList_getObjectByIndex;
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            Pump.NoteListSelection(__instance);
        }

        /// <summary>Current object of a list, read at time of use; null-safe.</summary>
        public static object CurrentObjectOf(object list)
        {
            try
            {
                if (Seams.SkaldObjectList_getCurrentObject == null) return null;
                return Seams.SkaldObjectList_getCurrentObject.Invoke(list, null);
            }
            catch { return null; }
        }

        /// <summary>The object's rendered list name, cleaned (getListName lives
        /// on SkaldBaseObject with overrides; getName is the registry fallback).</summary>
        public static string ListNameOf(object obj)
        {
            try
            {
                if (Seams.SkaldBaseObject_getListName == null) return null;
                string raw = Seams.SkaldBaseObject_getListName.Invoke(obj, null) as string;
                if (string.IsNullOrWhiteSpace(raw)) return null;
                string cleaned = TextCleaner.CleanText(raw);
                return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
            }
            catch { return null; }
        }
    }
}

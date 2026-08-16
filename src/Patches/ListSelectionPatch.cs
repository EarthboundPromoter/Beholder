using HarmonyLib;
using System;
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
    /// </summary>
    [HarmonyPatch]
    public static class ListSelectionPatch
    {
        private static MethodInfo _getCurrentObject;
        private static MethodInfo _getListName;
        private static bool _initialized;

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> TargetMethods()
        {
            var listType = AccessTools.TypeByName("SkaldObjectList");
            if (listType == null)
            {
                Plugin.Logger?.LogError("[ListSel] SkaldObjectList not found — selection state unvoiced");
                yield break;
            }
            var set = AccessTools.Method(listType, "setCurrentObject");
            var byIndex = AccessTools.Method(listType, "getObjectByIndex");
            if (set != null) yield return set;
            if (byIndex != null) yield return byIndex;
            Plugin.Logger?.LogInfo("[ListSel] Patching SkaldObjectList.setCurrentObject/getObjectByIndex");
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
                if (!_initialized) Initialize();
                if (_getCurrentObject == null) return null;
                return _getCurrentObject.Invoke(list, null);
            }
            catch { return null; }
        }

        /// <summary>The object's rendered list name, cleaned. Falls back to
        /// getName (getListName lives on SkaldBaseObject with overrides).</summary>
        public static string ListNameOf(object obj)
        {
            try
            {
                if (!_initialized) Initialize();
                string raw = null;
                if (_getListName != null)
                    raw = _getListName.Invoke(obj, null) as string;
                if (string.IsNullOrWhiteSpace(raw)) return null;
                string cleaned = TextCleaner.CleanText(raw);
                return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
            }
            catch { return null; }
        }

        private static void Initialize()
        {
            _initialized = true;
            var listType = AccessTools.TypeByName("SkaldObjectList");
            var objType = AccessTools.TypeByName("SkaldBaseObject");
            if (listType != null) _getCurrentObject = AccessTools.Method(listType, "getCurrentObject");
            if (objType != null)
                _getListName = AccessTools.Method(objType, "getListName") ?? AccessTools.Method(objType, "getName");
            if (_getCurrentObject == null || _getListName == null)
                Plugin.Logger?.LogError("[ListSel] Reflection incomplete — selection state unvoiced");
        }
    }
}

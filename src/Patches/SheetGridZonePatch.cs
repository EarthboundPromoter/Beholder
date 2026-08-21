using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Sheet-grid zones (owner ruling 2026-08-17): the Spell Grimoire's spell
    /// grid and the Abilities sheet's three ability grids are dormant
    /// UIAbilitySelectorGrid cursors that no input path reaches — mouse-only
    /// for everyone, controller included. Unlike combat (WP9), the grids
    /// coexist permanently with the sheet's text rows, so grid entry is a
    /// zone-crossing gesture, not a modal: A/D crosses rows ↔ grids (sideways
    /// natively no-ops on these sheets — the gesture is free), W/S steps
    /// vertically inside the active zone, and the A/D edges of the grid chain
    /// cross onward (Abilities: maneuvers → triggered → passive) or back to
    /// the rows.
    ///
    /// Mechanism, port-first:
    ///  - Wire: prefix on SheetClass.getControllerScrollableList returns the
    ///    active zone grid, so the native funnel (W/S), the B4 edge observer,
    ///    and composition all walk the grid while a zone is active. Cleared
    ///    on every state transition.
    ///  - Sideways: prefix on the outer GUIControl.controllerScrollSideways*
    ///    — enters a grid from the rows, steps ±1 inside a grid through the
    ///    game's own index setter, crosses/exits at the ends. Every other
    ///    screen (the CC editor's plus/minus flip included) passes through
    ///    untouched.
    ///  - Hover speech: every landing writes the grid's selection index, so
    ///    the selection join speaks the cell's name + count (names resolved
    ///    from the sheet's own spell/ability lists — index-aligned by
    ///    construction, the WP9 guarantee). Clicking a cell natively sets the
    ///    sheet's currentObject, whose description the game renders into the
    ///    already-hooked SheetDesc pane — click speech comes free.
    ///  - Ability grids carry the game's own rendered section header
    ///    ("Maneuver Abilities") as a zone label on entry.
    /// Seam-gated (WP8).
    /// </summary>
    public static class SheetGridZonePatch
    {
        private static object _gui;      // the owning GUIControl while a zone is active
        private static object _sheet;    // the sheet the zone was entered from
        private static object _zoneGrid; // the active grid; null = rows zone
        private static int _zoneSlot = -1; // which sheet field the grid came from
                                           // (0 = spellbook; 1..3 = ability grids)

        internal static void OnStateTransition()
        {
            _gui = null;
            _sheet = null;
            _zoneGrid = null;
            _zoneSlot = -1;
        }

        /// <summary>The sheets rebuild their grid OBJECTS in place on a
        /// party-member portrait swap (addEntries allocates new grids; no
        /// state change fires) — a captured grid reference goes stale and
        /// detached (adversarial review F1). Re-validate against the live
        /// field every use: re-seat onto the new grid audibly, or drop the
        /// zone when the slot emptied. Returns true when the zone is live.</summary>
        private static bool ReconcileZone()
        {
            if (_zoneGrid == null || _sheet == null) return false;
            try
            {
                FieldInfo slot = SlotField(_zoneSlot);
                if (slot == null) { OnStateTransition(); return false; }
                object live = slot.GetValue(_sheet);
                if (ReferenceEquals(live, _zoneGrid)) return true;
                if (live == null || CountOf(live) == 0) { OnStateTransition(); return false; }
                _zoneGrid = live;
                string label = GridLabel(_sheet, live);
                if (label != null) Pump.NoteZoneLabel(label);
                Seams.UICanvas_setCurrentSelectedButton.Invoke(live, new object[] { 0 });
                Snap(_gui, live);
                return true;
            }
            catch { OnStateTransition(); return false; }
        }

        private static FieldInfo SlotField(int slot)
        {
            switch (slot)
            {
                case 0: return Seams.SpellBookSheet_grid;
                case 1: return Seams.AbilitySheet_gridManeuvers;
                case 2: return Seams.AbilitySheet_gridTriggered;
                case 3: return Seams.AbilitySheet_gridPassive;
                default: return null;
            }
        }

        private static int SlotOf(object sheet, object grid)
        {
            try
            {
                if (Seams.UISpellBookSheetType != null && Seams.UISpellBookSheetType.IsInstanceOfType(sheet))
                    return 0;
                if (IsField(Seams.AbilitySheet_gridManeuvers, sheet, grid)) return 1;
                if (IsField(Seams.AbilitySheet_gridTriggered, sheet, grid)) return 2;
                if (IsField(Seams.AbilitySheet_gridPassive, sheet, grid)) return 3;
            }
            catch { }
            return -1;
        }

        private static bool Ready =>
            Seams.SheetClass_getControllerScrollableList != null
            && Seams.GUIControl_getControllerScrollableList != null
            && Seams.UICanvas_getScrollableElements != null
            && Seams.UICanvas_setCurrentSelectedButton != null
            && Seams.UICanvas_getCurrentSelectedButtonIndex != null
            && Seams.GUIControl_setMouseToUIElement != null
            && (Seams.UISpellBookSheetType != null || Seams.UIAbilitySheetType != null);

        // ---- The wire ----

        [HarmonyPatch]
        public static class ZoneWireHook
        {
            [HarmonyPrepare]
            static bool Prepare()
            {
                if (!Ready)
                {
                    Plugin.Logger?.LogWarning("[SheetZone] seams missing — sheet grids stay mouse-only");
                    return false;
                }
                return true;
            }

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.SheetClass_getControllerScrollableList;

            [HarmonyPrefix]
            static bool Prefix(ref UICanvas __result)
            {
                if (_zoneGrid == null) return true;
                if (!ReconcileZone()) return true; // zone dropped — native list
                if (_zoneGrid is UICanvas grid)
                {
                    __result = grid;
                    return false;
                }
                return true;
            }
        }

        // ---- Sideways: the zone gesture ----

        [HarmonyPatch]
        public static class SidewaysLeftHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready && Seams.GUIControl_controllerScrollSidewaysLeft != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_controllerScrollSidewaysLeft;

            [HarmonyPrefix]
            static bool Prefix(object __instance) => !Sideways(__instance, right: false);
        }

        [HarmonyPatch]
        public static class SidewaysRightHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready && Seams.GUIControl_controllerScrollSidewaysRight != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_controllerScrollSidewaysRight;

            [HarmonyPrefix]
            static bool Prefix(object __instance) => !Sideways(__instance, right: true);
        }

        /// <summary>Returns true when the press was handled (skip native).</summary>
        private static bool Sideways(object gui, bool right)
        {
            // Table gate C (2026-08-21): while the table engine owns this
            // state, the zone layer stands down whole — the table's A/D claim
            // means this path shouldn't be reachable at all (the choke
            // swallows the keys), so this guard is the belt to that brace.
            // With Tables.Engine off, the zone layer works exactly as before.
            if (TableCursor.OwnsCurrentState()) return false;
            try
            {
                // What the funnel currently walks (virtual, wire-aware).
                object list = Seams.GUIControl_getControllerScrollableList.Invoke(gui, null);
                if (list == null) return false;

                if (_zoneGrid != null && ReferenceEquals(list, _zoneGrid))
                {
                    StepOrCross(gui, right);
                    return true;
                }

                bool zoneSheet =
                    (Seams.UISpellBookSheetType != null && Seams.UISpellBookSheetType.IsInstanceOfType(list))
                    || (Seams.UIAbilitySheetType != null && Seams.UIAbilitySheetType.IsInstanceOfType(list));
                if (!zoneSheet) return false; // every other screen stays native

                var grids = GridsOf(list);
                if (grids.Count == 0) return false;
                Enter(gui, list, right ? grids[0] : grids[grids.Count - 1], atEnd: !right);
                return true;
            }
            catch { return false; }
        }

        private static void StepOrCross(object gui, bool right)
        {
            object grid = _zoneGrid;
            int index = (int)Seams.UICanvas_getCurrentSelectedButtonIndex.Invoke(grid, null);
            int count = CountOf(grid);

            if (right && index < count - 1)
            {
                Seams.UICanvas_setCurrentSelectedButton.Invoke(grid, new object[] { index + 1 });
                Snap(gui, grid);
                return;
            }
            if (!right && index > 0)
            {
                Seams.UICanvas_setCurrentSelectedButton.Invoke(grid, new object[] { index - 1 });
                Snap(gui, grid);
                return;
            }

            // At the grid's linear edge: cross onward, or back to the rows.
            var grids = GridsOf(_sheet);
            int at = grids.IndexOf(grid);
            int next = right ? at + 1 : at - 1;
            if (next >= 0 && next < grids.Count)
            {
                Enter(gui, _sheet, grids[next], atEnd: !right);
                return;
            }
            if (!right && RowCountOf(_sheet) > 0)
            {
                ExitToRows(gui);
                return;
            }
            Scaffold.SpeechService.Say(right ? "Bottom of list." : "Top of list.", "Nav");
        }

        private static void Enter(object gui, object sheet, object grid, bool atEnd)
        {
            int count = CountOf(grid);
            if (count == 0) return; // GridsOf filters these; defensive only
            _gui = gui;
            _sheet = sheet;
            _zoneGrid = grid;
            _zoneSlot = SlotOf(sheet, grid);
            string label = GridLabel(sheet, grid);
            if (label != null) Pump.NoteZoneLabel(label);
            Seams.UICanvas_setCurrentSelectedButton.Invoke(grid, new object[] { atEnd ? count - 1 : 0 });
            Snap(gui, grid);
        }

        private static void ExitToRows(object gui)
        {
            object sheet = _sheet;
            _zoneGrid = null;
            _zoneSlot = -1;
            int rows = RowCountOf(sheet);
            if (rows <= 0) return;
            int index = (int)Seams.UICanvas_getCurrentSelectedButtonIndex.Invoke(sheet, null);
            if (index < 0) index = 0;
            if (index >= rows) index = rows - 1;
            Seams.UICanvas_setCurrentSelectedButton.Invoke(sheet, new object[] { index });
            // The sheet family's own snap offsets (GUIControl.setMouseToSelectedOption).
            try { Seams.GUIControl_setMouseToUIElement.Invoke(gui, new object[] { sheet, 2, -6 }); }
            catch { }
        }

        /// <summary>Explicit cell snap with the grid family's offsets (the
        /// native post-move snap uses the sheet's 2,-6, which may miss a
        /// cell); also applied after native W/S moves via the postfix below.</summary>
        private static void Snap(object gui, object grid)
        {
            try { Seams.GUIControl_setMouseToUIElement.Invoke(gui, new object[] { grid, 8, -8 }); }
            catch { }
        }

        [HarmonyPatch]
        public static class VerticalSnapAboveHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready && Seams.GUIControl_setMouseToClosestOptionAbove != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_setMouseToClosestOptionAbove;

            [HarmonyPostfix]
            static void Postfix(object __instance)
            {
                if (_zoneGrid != null) Snap(__instance, _zoneGrid);
            }
        }

        [HarmonyPatch]
        public static class VerticalSnapBelowHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready && Seams.GUIControl_setMouseToClosestOptionBelow != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_setMouseToClosestOptionBelow;

            [HarmonyPostfix]
            static void Postfix(object __instance)
            {
                if (_zoneGrid != null) Snap(__instance, _zoneGrid);
            }
        }

        // ---- Composition support ----

        /// <summary>Cell name from the owning sheet's own data list —
        /// index-aligned with the grid by construction (the sheets build the
        /// grids from these exact lists). Null when the grid isn't ours.</summary>
        internal static string NameAt(object grid, int index)
        {
            try
            {
                object sheet = _sheet;
                if (sheet == null || grid == null || index < 0) return null;

                System.Collections.IList list = null;
                if (Seams.UISpellBookSheetType != null && Seams.UISpellBookSheetType.IsInstanceOfType(sheet))
                {
                    if (Seams.SpellBookSheet_grid != null
                        && ReferenceEquals(Seams.SpellBookSheet_grid.GetValue(sheet), grid))
                    {
                        object spellList = Seams.SpellBookSheet_spellList?.GetValue(sheet);
                        list = spellList != null ? Seams.SpellList_spells?.GetValue(spellList) as System.Collections.IList : null;
                    }
                }
                else if (Seams.UIAbilitySheetType != null && Seams.UIAbilitySheetType.IsInstanceOfType(sheet))
                {
                    if (IsField(Seams.AbilitySheet_gridManeuvers, sheet, grid))
                        list = Seams.AbilitySheet_maneuverList?.GetValue(sheet) as System.Collections.IList;
                    else if (IsField(Seams.AbilitySheet_gridTriggered, sheet, grid))
                        list = Seams.AbilitySheet_triggeredList?.GetValue(sheet) as System.Collections.IList;
                    else if (IsField(Seams.AbilitySheet_gridPassive, sheet, grid))
                        list = Seams.AbilitySheet_passiveList?.GetValue(sheet) as System.Collections.IList;
                }

                if (list == null || index >= list.Count) return null;
                return Seams.SkaldBaseObject_getName?.Invoke(list[index], null) as string;
            }
            catch { return null; }
        }

        private static bool IsField(FieldInfo field, object owner, object grid)
            => field != null && ReferenceEquals(field.GetValue(owner), grid);

        // ---- Structure reads ----

        private static readonly List<object> _gridsScratch = new List<object>();

        /// <summary>The sheet's grids in visual order, empties filtered.</summary>
        private static List<object> GridsOf(object sheet)
        {
            _gridsScratch.Clear();
            try
            {
                if (Seams.UISpellBookSheetType != null && Seams.UISpellBookSheetType.IsInstanceOfType(sheet))
                {
                    AddGrid(Seams.SpellBookSheet_grid?.GetValue(sheet));
                }
                else if (Seams.UIAbilitySheetType != null && Seams.UIAbilitySheetType.IsInstanceOfType(sheet))
                {
                    AddGrid(Seams.AbilitySheet_gridManeuvers?.GetValue(sheet));
                    AddGrid(Seams.AbilitySheet_gridTriggered?.GetValue(sheet));
                    AddGrid(Seams.AbilitySheet_gridPassive?.GetValue(sheet));
                }
            }
            catch { }
            return _gridsScratch;
        }

        private static void AddGrid(object grid)
        {
            if (grid != null && CountOf(grid) > 0) _gridsScratch.Add(grid);
        }

        private static int CountOf(object canvas)
        {
            try
            {
                var elements = Seams.UICanvas_getScrollableElements.Invoke(canvas, null)
                    as System.Collections.ICollection;
                return elements?.Count ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>Scrollable row count of the sheet itself — read through
        /// the sheet's own override, not the wire.</summary>
        private static int RowCountOf(object sheet) => sheet == null ? 0 : CountOf(sheet);

        /// <summary>The grid's rendered section header (the Abilities sheet
        /// draws a text block above each grid — the game's own label). Null
        /// when there is none (the Grimoire's single grid needs no label).</summary>
        private static string GridLabel(object sheet, object grid)
        {
            try
            {
                if (Seams.BaseCharacterSheet_leftColumn == null || Seams.UICanvas_getElements == null)
                    return null;
                object leftColumn = Seams.BaseCharacterSheet_leftColumn.GetValue(sheet);
                if (leftColumn == null) return null;
                var elements = Seams.UICanvas_getElements.Invoke(leftColumn, null) as System.Collections.IList;
                if (elements == null) return null;
                int at = -1;
                for (int i = 0; i < elements.Count; i++)
                    if (ReferenceEquals(elements[i], grid)) { at = i; break; }
                // Only the immediately preceding element can be the header block.
                if (at > 0 && elements[at - 1] is UITextBlock)
                {
                    string raw = Seams.UITextBlock_content.GetValue(elements[at - 1]) as string;
                    string cleaned = string.IsNullOrWhiteSpace(raw) ? null : TextCleaner.CleanText(raw);
                    return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
                }
                return null;
            }
            catch { return null; }
        }
    }
}

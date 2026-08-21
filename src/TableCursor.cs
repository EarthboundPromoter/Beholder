using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SkaldAccessibility
{
    /// <summary>
    /// The table-UI engine (docs\table-ui-design.md — R15; owner build
    /// go-ahead 2026-08-21). Gate A+B: the clean single-list family. Gate C:
    /// the sheet family (character / attributes / abilities / spellbook) —
    /// the engine's first 2D section geometry, row flow, header-skip rule,
    /// facet layer, and bespoke row composition.
    ///
    /// Grammar: ARROWS walk rows; W/S walks sections within the current
    /// rendered column; A/D crosses columns (remembered section per column);
    /// LEFT/RIGHT walks the current row's facets (facet 0 = the row line;
    /// parked facet persists across rows within a section). Z/X, numbers,
    /// Escape, Q/E stay native. No mod-side focus (R2): row position is the
    /// canvas's own currentSelectedButton + the virtual-mouse park.
    ///
    /// Two row-walk modes:
    ///  - FUNNEL (gate-B lists): drive the game's own
    ///    setMouseToClosestOptionAbove/Below — native selection, native
    ///    paged-window slide, the shipped edge observer + hover join speak.
    ///  - INDEX (sheet sections): seat the canvas's own selected button
    ///    directly and park (the SheetGridZonePatch idiom) — the sheet pages
    ///    have no paging, the grids' native scroll predicates are broken
    ///    below 12 cells (UIAbilitySelectorGrid.cs:179-188), and the native
    ///    increment is hover-driven. Index rows FLOW across section
    ///    boundaries (§3: the seam never feels like a wall) — landing
    ///    announced via the zone-label slot.
    ///
    /// Header rule (§6.4, owner ruling): an index-0 header pseudo-row is the
    /// SECTION LABEL, never a row — the walk skips it; the spoken label is
    /// harvested from it verbatim. An "-Empty-"/"--Empty--" pseudo-row means
    /// the section censuses as "none" and holds no rows.
    ///
    /// Patch shape (receipt 7): W/A/S/D + arrows swallowed at the SkaldIO
    /// choke AND every left-stick read forced false while active. Suspends
    /// whole under popups, selector grids, and text entry (the R10 lesson).
    /// SheetGridZonePatch stands down while this engine owns its states
    /// (OwnsCurrentState — structural: with A/D swallowed its entry gesture
    /// never fires; the explicit guard covers the residue). All wordings
    /// provisional until owner calibration.
    /// </summary>
    public static class TableCursor
    {
        // ---- config ----
        private static BepInEx.Configuration.ConfigEntry<bool> _enabled;

        public static void BindConfig(BepInEx.Configuration.ConfigFile config)
        {
            _enabled = config.Bind("Tables", "Engine", true,
                "Table navigation on standard UI screens (table-ui-design R15): arrows walk rows, "
                + "W/S walks sections, A/D crosses columns, Left/Right walks facets. Off restores native-only nav.");
        }

        // =====================================================================
        // Screen registries
        // =====================================================================

        // Gate B: single-list family. Value = the primary section's label.
        private static readonly Dictionary<string, string> SimpleScreens
            = new Dictionary<string, string>
        {
            { "MenuState",       "Menu" },
            { "LoadMenuState",   "Saves" },
            { "SaveMenuState",   "Saves" },
            { "LoadModuleState", "Modules" },
            { "JournalState",    "Entries" },
            { "QuestState",      "Entries" },
            { "FactionsState",   "Entries" },
        };
        private const string ButtonsLabel = "Buttons";

        // Gate C: sheet family — explicit section geometry.
        private sealed class SectionDef
        {
            public string Id;
            public string Label;            // null → harvest the index-0 header verbatim
            public int Column;              // 0 left, 1 right, -1 shared tail (buttons)
            public bool SkipHeader;         // index-0 header pseudo-row = the label, not a row
            public string FacetData;        // Character list-getter key for the description facet
            public bool Grid;               // ability/spell grid: bespoke row composition
            public Func<object, object> Canvas; // sheet instance → canvas (null = gui numericButtons)
        }

        private static readonly Dictionary<string, SectionDef[]> SheetScreens
            = new Dictionary<string, SectionDef[]>
        {
            // §6.4 character page: Descriptors ↔ Conditions on A/D, Buttons on S.
            { "CharacterState", new[]
                {
                    new SectionDef { Id = "descriptors", Label = "Descriptors", Column = 0,
                        Canvas = s => Seams.CharSheet_entrySlot1?.GetValue(s) },
                    new SectionDef { Id = "conditions", Column = 1, SkipHeader = true, FacetData = "conditions",
                        Canvas = s => Seams.CharSheet_entrySlot2?.GetValue(s) },
                    new SectionDef { Id = "buttons", Label = ButtonsLabel, Column = -1 },
                } },
            // §6.4 attributes page: five headed tables in the rendered 2+3
            // geometry (labels harvested from the game's own headers —
            // "Primary Stats" / "Skills" / "Secondary Stats" / "Combat
            // Stats" / "Defences"), descriptions lateral.
            { "AttributeState", new[]
                {
                    new SectionDef { Id = "primary", Column = 0, SkipHeader = true, FacetData = "primary",
                        Canvas = s => Seams.CharSheet_entrySlot1?.GetValue(s) },
                    new SectionDef { Id = "skills", Column = 0, SkipHeader = true, FacetData = "skills",
                        Canvas = s => Seams.CharSheet_entrySlot2?.GetValue(s) },
                    new SectionDef { Id = "secondary", Column = 1, SkipHeader = true, FacetData = "secondary",
                        Canvas = s => Seams.CharSheet_entrySlot3?.GetValue(s) },
                    new SectionDef { Id = "combat", Column = 1, SkipHeader = true, FacetData = "combat",
                        Canvas = s => Seams.CharSheet_entrySlot4?.GetValue(s) },
                    new SectionDef { Id = "defences", Column = 1, SkipHeader = true, FacetData = "defences",
                        Canvas = s => Seams.CharSheet_entrySlot5?.GetValue(s) },
                    new SectionDef { Id = "buttons", Label = ButtonsLabel, Column = -1 },
                } },
            // §6.6 spellbook: geometry as drawn — left: Magic Attributes ↔
            // Spells ↔ Buttons; right: Spell Schools alone.
            { "SpellsState", new[]
                {
                    new SectionDef { Id = "magicattrs", Column = 0, SkipHeader = true, FacetData = "magicattrs",
                        Canvas = s => Seams.CharSheet_entrySlot1?.GetValue(s) },
                    new SectionDef { Id = "spells", Label = "Spells", Column = 0, Grid = true,
                        Canvas = s => Seams.SpellBookSheet_grid?.GetValue(s) },
                    new SectionDef { Id = "schools", Column = 1, SkipHeader = true, FacetData = "schools",
                        Canvas = s => Seams.CharSheet_entrySlot2?.GetValue(s) },
                    new SectionDef { Id = "buttons", Label = ButtonsLabel, Column = -1 },
                } },
            // §6.5 ability sheet: three typed tables (the game's own rendered
            // headers, verbatim), single column, select-only Z.
            { "AbilitiesState", new[]
                {
                    new SectionDef { Id = "maneuvers", Label = "Maneuver Abilities", Column = 0, Grid = true,
                        Canvas = s => Seams.AbilitySheet_gridManeuvers?.GetValue(s) },
                    new SectionDef { Id = "triggered", Label = "Triggered Abilities", Column = 0, Grid = true,
                        Canvas = s => Seams.AbilitySheet_gridTriggered?.GetValue(s) },
                    new SectionDef { Id = "passives", Label = "Passive Bonus Abilities", Column = 0, Grid = true,
                        Canvas = s => Seams.AbilitySheet_gridPassive?.GetValue(s) },
                    new SectionDef { Id = "buttons", Label = ButtonsLabel, Column = -1 },
                } },
        };

        // =====================================================================
        // Live screen memo
        // =====================================================================

        private static int _frame = -1;
        private static object _state;
        private static object _gui;
        private static string _simpleLabel;      // gate-B screen
        private static SectionDef[] _sheetDef;   // gate-C screen

        private static object _redirectCanvas;   // current non-native section canvas
        private static bool _resolving;          // re-entrancy guard for NativeScrollableList
        private static int _facet;               // parked facet (0 = the row line)
        private static readonly string[] _colRemember = new string[2];
        private static int _armedLogFrame = -1;

        private static bool Refresh()
        {
            if (Time.frameCount == _frame) return _simpleLabel != null || _sheetDef != null;
            _frame = Time.frameCount;
            _state = null; _gui = null; _simpleLabel = null; _sheetDef = null;
            if (_enabled == null || !_enabled.Value) return false;
            try
            {
                object s = Pump.CurrentStateObject();
                if (s == null) return false;
                string name = s.GetType().Name;
                bool simple = SimpleScreens.TryGetValue(name, out string label);
                bool sheet = !simple && SheetScreens.TryGetValue(name, out _sheetDef);
                if (!simple && !sheet) { _sheetDef = null; return false; }
                object gui = Seams.StateBase_guiControl?.GetValue(s);
                if (gui == null) { _sheetDef = null; return false; }
                _state = s; _gui = gui;
                if (simple) _simpleLabel = label;
                return true;
            }
            catch { _sheetDef = null; return false; }
        }

        private static bool PopupUp()
        {
            try
            {
                return Seams.PopUpControl_getCurrentPopUp != null
                    && Seams.PopUpControl_getCurrentPopUp.Invoke(null, null) != null;
            }
            catch { return false; }
        }

        /// <summary>Registered screen, config on, gui resolvable — ignoring
        /// suspension. SheetGridZonePatch consults this to stand down.</summary>
        internal static bool OwnsCurrentState() => Refresh();

        /// <summary>The whole modality: owned screen AND no excursion owns
        /// the input (the R10 suspension pattern).</summary>
        internal static bool Active()
        {
            if (!Refresh()) return false;
            if (Patches.ControllerFeedPatch.TextEntryActive()) return false;
            if (PopupUp() || Patches.GridNavigationPatch.GridActive()) return false;
            return true;
        }

        /// <summary>Force-false half of the claim (first guard in every
        /// left-stick postfix, beside the combat latch's).</summary>
        internal static bool ClaimsStick => Active();

        /// <summary>Binding-route half: the game goes blind to arrows and
        /// W/A/S/D while the table is live. Escape, numbers, Z/X, Q/E stay
        /// native.</summary>
        public static bool ShouldSwallowKey(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                case KeyCode.LeftArrow:
                case KeyCode.RightArrow:
                case KeyCode.W:
                case KeyCode.A:
                case KeyCode.S:
                case KeyCode.D:
                    return Active();
                default:
                    return false;
            }
        }

        /// <summary>Section redirect, consulted by the SheetClass
        /// getControllerScrollableList prefix. Re-validated on read.</summary>
        internal static object ScrollableListOverride(object guiInstance)
        {
            if (_resolving) return null;
            object canvas = _redirectCanvas;
            if (canvas == null) return null;
            if (!Active()) return null;
            if (!ReferenceEquals(guiInstance, _gui)) return null;
            if (ScrollableCount(canvas) == 0) return null;
            return canvas;
        }

        // =====================================================================
        // Input
        // =====================================================================

        public static bool ProcessInput()
        {
            if (!Active()) return false;

            bool up = Input.GetKeyDown(KeyCode.UpArrow);
            bool down = Input.GetKeyDown(KeyCode.DownArrow);
            bool left = Input.GetKeyDown(KeyCode.LeftArrow);
            bool right = Input.GetKeyDown(KeyCode.RightArrow);
            bool w = Input.GetKeyDown(KeyCode.W);
            bool a = Input.GetKeyDown(KeyCode.A);
            bool s = Input.GetKeyDown(KeyCode.S);
            bool d = Input.GetKeyDown(KeyCode.D);
            if (!(up || down || left || right || w || a || s || d)) return false;

            try
            {
                if (up || down) RowStep(up);
                else if (left || right) FacetStep(left ? -1 : +1);
                else if (w || s) SectionStep(w ? -1 : +1);
                else ColumnStep(a ? -1 : +1);
            }
            catch (Exception ex) { Scaffold.Log.Throttled("table.input", ex.Message); }
            return true;
        }

        public static void OnStateTransition()
        {
            _redirectCanvas = null;
            _facet = 0;
            _colRemember[0] = null; _colRemember[1] = null;
            if (!Refresh()) return;
            if (_armedLogFrame == Time.frameCount) return;
            _armedLogFrame = Time.frameCount;
            try
            {
                var sections = ResolveSections();
                var parts = new List<string>();
                foreach (var sec in sections) parts.Add($"{sec.Label}={sec.Count}");
                Scaffold.Log.Debug("Gate",
                    $"table screen: {_state.GetType().Name} sections=[{string.Join(", ", parts)}]");
            }
            catch { }
        }

        // =====================================================================
        // Sections (resolved live, memoized per frame)
        // =====================================================================

        private sealed class Section
        {
            public SectionDef Def;      // null on gate-B screens
            public string Id;
            public string Label;
            public object Canvas;
            public int Start;           // first walkable row (header skipped)
            public int Count;           // walkable rows
            public int Column;
            public bool IndexRows;
            public bool IsGrid;
        }

        private static int _secFrame = -1;
        private static List<Section> _sections;

        private static List<Section> ResolveSections()
        {
            if (Time.frameCount == _secFrame && _sections != null) return _sections;
            _secFrame = Time.frameCount;
            _sections = _sheetDef != null ? ResolveSheetSections() : ResolveSimpleSections();
            return _sections;
        }

        private static List<Section> ResolveSimpleSections()
        {
            var sections = new List<Section>(2);
            object primary = NativeScrollableList();
            int primaryCount = ScrollableCount(primary);
            if (primary != null && primaryCount > 0)
                sections.Add(new Section { Id = "primary", Label = _simpleLabel, Canvas = primary,
                    Start = 0, Count = primaryCount, Column = 0, IndexRows = false });

            object numeric = NumericButtons();
            if (numeric != null && !ReferenceEquals(numeric, primary))
            {
                int numericCount = ScrollableCount(numeric);
                if (numericCount > 0)
                    sections.Add(new Section { Id = "buttons", Label = ButtonsLabel, Canvas = numeric,
                        Start = 0, Count = numericCount, Column = 0, IndexRows = false });
            }
            return sections;
        }

        /// <summary>Sheet sections resolve against the live sheet instance
        /// (the native scrollable list on every sheet page). Null/empty
        /// canvases drop out of the walk (R4) — EXCEPT sections whose
        /// emptiness is a census fact (conditions "none", zero-filtered
        /// school lists): those stay as zero-row stops.</summary>
        private static List<Section> ResolveSheetSections()
        {
            var sections = new List<Section>(_sheetDef.Length);
            object sheet = NativeScrollableList();
            if (sheet == null) return sections;

            foreach (var def in _sheetDef)
            {
                object canvas;
                if (def.Canvas == null) canvas = NumericButtons();
                else { try { canvas = def.Canvas(sheet); } catch { canvas = null; } }
                if (canvas == null) continue;

                int total = ScrollableCount(canvas);
                if (total == 0 && def.Canvas != null && !def.Grid && !def.SkipHeader) continue;
                int start = def.SkipHeader && total > 0 ? 1 : 0;
                int count = total - start;
                // The "-Empty-"/"--Empty--" pseudo-row: a rendered placeholder,
                // not a row — the section censuses as none.
                if (count == 1 && IsEmptyPseudoRow(canvas, start)) count = 0;
                if (count == 0 && def.Canvas == null) continue; // no buttons at all

                string label = def.Label ?? HarvestHeader(canvas) ?? def.Id;
                sections.Add(new Section { Def = def, Id = def.Id, Label = label, Canvas = canvas,
                    Start = start, Count = count, Column = def.Column,
                    IndexRows = true, IsGrid = def.Grid });
            }
            return sections;
        }

        private static object NativeScrollableList()
        {
            if (Seams.GUIControl_getControllerScrollableList == null || _gui == null) return null;
            _resolving = true;
            try { return Seams.GUIControl_getControllerScrollableList.Invoke(_gui, null); }
            catch { return null; }
            finally { _resolving = false; }
        }

        private static object NumericButtons()
        {
            try { return Seams.GUIControl_numericButtons?.GetValue(_gui); }
            catch { return null; }
        }

        private static int CurrentSectionIndex(List<Section> sections)
        {
            object redirect = _redirectCanvas;
            if (redirect != null)
            {
                for (int i = 0; i < sections.Count; i++)
                    if (ReferenceEquals(sections[i].Canvas, redirect)) return i;
                _redirectCanvas = null; // vanished — stand on the first section
            }
            return 0;
        }

        // =====================================================================
        // Rows
        // =====================================================================

        private static void RowStep(bool upward)
        {
            var sections = ResolveSections();
            if (sections.Count == 0) { Scaffold.SpeechService.Say("No sections.", "Nav"); return; }
            int cur = CurrentSectionIndex(sections);
            var sec = sections[cur];

            if (!sec.IndexRows)
            {
                // Gate-B funnel path: native selection, native window slide,
                // edge observer + hover join speak. Unchanged since gate B.
                Pump.NotePlayerNav();
                var funnel = upward
                    ? Seams.GUIControl_setMouseToClosestOptionAbove
                    : Seams.GUIControl_setMouseToClosestOptionBelow;
                funnel?.Invoke(_gui, null);
                Park(sec);
                return;
            }

            if (sec.Count == 0)
            {
                Scaffold.SpeechService.Say($"{sec.Label}, none.", "Nav");
                return;
            }

            int idx = CurrentIndex(sec.Canvas);
            if (idx < sec.Start || idx >= sec.Start + sec.Count)
            {
                // First touch of this section: adopt it at its first row.
                AdoptSection(sec);
                Seat(sec, sec.Start);
                return;
            }

            int next = idx + (upward ? -1 : +1);
            if (next < sec.Start)
            {
                // Row flow (§3): cross the seam to the previous section's
                // last row — announced via the landing's zone label.
                if (cur > 0) LandSection(sections[cur - 1], atLastRow: true);
                else Scaffold.SpeechService.Say("Top of list.", "Nav");
                return;
            }
            if (next >= sec.Start + sec.Count)
            {
                if (cur < sections.Count - 1) LandSection(sections[cur + 1], atLastRow: false, atFirstRow: true);
                else Scaffold.SpeechService.Say("Bottom of list.", "Nav");
                return;
            }
            AdoptSection(sec);
            Seat(sec, next);
        }

        /// <summary>Seat + park + player-nav stamp: the game's own setter
        /// fires the selection join, which speaks the row.</summary>
        private static void Seat(Section sec, int row)
        {
            Pump.NotePlayerNav();
            try { Seams.UICanvas_setCurrentSelectedButton?.Invoke(sec.Canvas, new object[] { row }); }
            catch { }
            Park(sec);
        }

        private static int CurrentIndex(object canvas)
        {
            try
            {
                if (Seams.UICanvas_currentSelectedButton != null)
                    return (int)Seams.UICanvas_currentSelectedButton.GetValue(canvas);
            }
            catch { }
            return -1;
        }

        /// <summary>Silent section adoption (redirect + column memory) for
        /// in-section row moves — no census, no label.</summary>
        private static void AdoptSection(Section sec)
        {
            object native = NativeScrollableList();
            _redirectCanvas = ReferenceEquals(sec.Canvas, native) ? null : sec.Canvas;
            if (sec.Column >= 0) _colRemember[sec.Column] = sec.Id;
        }

        // =====================================================================
        // Section / column travel
        // =====================================================================

        private static void SectionStep(int direction)
        {
            var sections = ResolveSections();
            if (sections.Count == 0) { Scaffold.SpeechService.Say("No sections.", "Nav"); return; }
            int cur = CurrentSectionIndex(sections);
            var sec = sections[cur];

            if (_sheetDef == null)
            {
                // Gate-B linear stack (unchanged).
                int next = cur + direction;
                if (next < 0) { Scaffold.SpeechService.Say("No section above.", "Nav"); return; }
                if (next >= sections.Count) { Scaffold.SpeechService.Say("No section below.", "Nav"); return; }
                LandSection(sections[next]);
                return;
            }

            // Sheet screens: W/S travels the current column's chain (shared
            // -1 sections ride every chain's tail — §3a "then buttons").
            var chain = new List<int>();
            int chainPos = -1;
            int col = sec.Column;
            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i].Column == col || sections[i].Column == -1 || col == -1)
                {
                    if (i == cur) chainPos = chain.Count;
                    chain.Add(i);
                }
            }
            int target = chainPos + direction;
            if (target < 0) { Scaffold.SpeechService.Say("No section above.", "Nav"); return; }
            if (target >= chain.Count) { Scaffold.SpeechService.Say("No section below.", "Nav"); return; }
            LandSection(sections[chain[target]]);
        }

        /// <summary>A/D on sheet screens: cross to the other rendered column's
        /// remembered section (§3a "left column ↔ right column").</summary>
        private static void ColumnStep(int direction)
        {
            if (_sheetDef == null)
            {
                Scaffold.SpeechService.Say(direction < 0 ? "No section left." : "No section right.", "Nav");
                return;
            }
            var sections = ResolveSections();
            if (sections.Count == 0) { Scaffold.SpeechService.Say("No sections.", "Nav"); return; }
            int cur = CurrentSectionIndex(sections);
            int col = sections[cur].Column;
            // Directional, geometrically faithful: A from the left column and
            // D from the right are edge refusals; the shared buttons tail
            // refuses laterally (W climbs out of it).
            int other = col < 0 ? -1 : col + direction;
            Section target = null;
            if (other == 0 || other == 1)
            {
                string remembered = _colRemember[other];
                foreach (var s in sections)
                {
                    if (s.Column != other) continue;
                    if (target == null) target = s;
                    if (remembered != null && s.Id == remembered) { target = s; break; }
                }
            }
            if (target == null)
            {
                Scaffold.SpeechService.Say(direction < 0 ? "No section left." : "No section right.", "Nav");
                return;
            }
            LandSection(target);
        }

        /// <summary>Section landing (R15): label + census ride the zone-label
        /// slot onto the restored row's own line; the remembered row is the
        /// canvas's OWN currentSelectedButton, clamped into the walkable
        /// range. Zero-row sections speak their census directly (no row line
        /// exists to carry the label).</summary>
        private static void LandSection(Section sec, bool atLastRow = false, bool atFirstRow = false)
        {
            AdoptSection(sec);
            _facet = 0;

            if (sec.Count == 0)
            {
                Pump.NotePlayerNav();
                Scaffold.SpeechService.Say($"{sec.Label}, none.", "Nav");
                Park(sec);
                Scaffold.Log.Debug("Gate", $"table section land: {sec.Label} count=0");
                return;
            }

            int row;
            if (atFirstRow) row = sec.Start;
            else if (atLastRow) row = sec.Start + sec.Count - 1;
            else
            {
                row = CurrentIndex(sec.Canvas);
                if (row < sec.Start) row = sec.Start;
                if (row >= sec.Start + sec.Count) row = sec.Start + sec.Count - 1;
            }

            Pump.NoteZoneLabel($"{sec.Label}, {sec.Count}");
            Seat(sec, row);
            Scaffold.Log.Debug("Gate", $"table section land: {sec.Label} count={sec.Count} row={row}");
        }

        // =====================================================================
        // Facets (Left/Right — R11's lateral scan; facet 0 = the row line)
        // =====================================================================

        private static void FacetStep(int direction)
        {
            var sections = ResolveSections();
            if (sections.Count == 0) { Scaffold.SpeechService.Say("No columns.", "Nav"); return; }
            var sec = sections[CurrentSectionIndex(sections)];
            int row = sec.IndexRows ? CurrentIndex(sec.Canvas) : -1;
            if (sec.IndexRows && (row < sec.Start || row >= sec.Start + sec.Count))
            {
                Scaffold.SpeechService.Say(sec.Count == 0 ? $"{sec.Label}, none." : "No columns.", "Nav");
                return;
            }

            var facets = FacetsOf(sec, row);
            if (facets == null || facets.Count == 0)
            {
                // Funnel sections: the one facet is the composed row line.
                string line = null;
                try { line = Pump.CurrentLineOf(CurrentCanvasVirtual()); } catch { }
                Scaffold.SpeechService.Say(line ?? "No columns.", "Nav");
                return;
            }

            _facet += direction;
            if (_facet < 0) _facet = 0;
            if (_facet >= facets.Count) _facet = facets.Count - 1;
            string text = facets[_facet];
            Scaffold.SpeechService.Say(string.IsNullOrWhiteSpace(text) ? "No columns." : text, "Nav");
        }

        /// <summary>The current row's ordered facet list; [0] = the row line.
        /// Null on funnel sections (gate B).</summary>
        private static List<string> FacetsOf(Section sec, int row)
        {
            if (sec.Def == null || row < 0) return null;
            if (sec.Id == "buttons") return null; // numeric rows: generic voice
            try
            {
                if (sec.IsGrid)
                {
                    var parts = GridRowParts(sec, row);
                    if (parts == null) return null;
                    var facets = new List<string> { JoinParts(parts) };
                    facets.AddRange(parts);
                    return facets;
                }
                var rowLine = ComposeEntryRow(sec, row);
                if (rowLine == null) return null;
                var list = new List<string> { rowLine };
                if (sec.Def.FacetData != null)
                {
                    string desc = FacetDescription(sec, row);
                    if (!string.IsNullOrWhiteSpace(desc)) list.Add(desc);
                }
                return list;
            }
            catch { return null; }
        }

        /// <summary>The lateral description facet: the row's own data object,
        /// re-resolved from the character's live list (the game rebuilds the
        /// SkaldDataList per call — never cached). Canvas row i is 1:1 with
        /// data row i (blanked surplus buttons drop off the scrollable tail;
        /// live rows keep order).</summary>
        private static string FacetDescription(Section sec, int row)
        {
            var getter = FacetGetter(sec.Def.FacetData);
            if (getter == null || Seams.SkaldObjectList_getObjectList == null
                || Seams.SkaldBaseObject_getFullDescription == null) return null;
            object sheet = NativeScrollableList();
            object character = Seams.CharSheet_currentCharacter?.GetValue(sheet);
            if (character == null) return null;
            object dataList = getter.Invoke(character, null);
            var objects = dataList == null ? null
                : Seams.SkaldObjectList_getObjectList.Invoke(dataList, null) as IList;
            if (objects == null || row < 0 || row >= objects.Count) return null;
            string desc = Seams.SkaldBaseObject_getFullDescription.Invoke(objects[row], null) as string;
            if (string.IsNullOrWhiteSpace(desc)) return null;
            return Patches.TextCleaner.CleanText(desc.Replace("\n", " "));
        }

        private static System.Reflection.MethodInfo FacetGetter(string key)
        {
            switch (key)
            {
                case "conditions": return Seams.Character_getListOfConditions;
                case "primary": return Seams.Character_getListOfPrimaryAttributes;
                case "skills": return Seams.Character_getListOfSkills;
                case "secondary": return Seams.Character_getListOfSecondaryAttributes;
                case "combat": return Seams.Character_getListOfCombatStats;
                case "defences": return Seams.Character_getListOfDefences;
                case "magicattrs": return Seams.Character_getListOfMagicAttributes;
                case "schools": return Seams.Character_getListOfSpellSchools;
                default: return null;
            }
        }

        // =====================================================================
        // Row composition (the ComposeSelection hook)
        // =====================================================================

        /// <summary>Called first from Pump.ComposeSelection: owns the rows of
        /// registered sheet sections (entry canvases and ability/spell
        /// grids). Null = not ours — generic composition proceeds.</summary>
        internal static string ComposeSheetCell(object control, int index)
        {
            try
            {
                if (!Refresh() || _sheetDef == null) return null;
                var sections = ResolveSections();

                // The sheet canvas's OWN flat index (a native write path —
                // e.g. the hover walk over the concatenated entry rows) maps
                // onto the same section rows, so either note path speaks the
                // identical line.
                object sheet = NativeScrollableList();
                if (ReferenceEquals(control, sheet))
                {
                    int cum = 0;
                    foreach (var sec in sections)
                    {
                        if (sec.IsGrid || sec.Id == "buttons") continue;
                        int total = ScrollableCount(sec.Canvas);
                        if (index < cum + total)
                        {
                            int local = index - cum;
                            if (local < sec.Start || local >= sec.Start + sec.Count) return null;
                            string entryLine = ComposeEntryRow(sec, local);
                            if (entryLine == null) return null;
                            int p = local - sec.Start + 1;
                            return sec.Count > 1 ? $"{entryLine}, {p} of {sec.Count}" : entryLine;
                        }
                        cum += total;
                    }
                    return null;
                }

                foreach (var sec in sections)
                {
                    if (!ReferenceEquals(sec.Canvas, control)) continue;
                    if (sec.Def == null || sec.Id == "buttons") return null;
                    if (index < sec.Start || index >= sec.Start + sec.Count) return null;
                    int pos = index - sec.Start + 1;
                    if (sec.IsGrid)
                    {
                        var parts = GridRowParts(sec, index);
                        if (parts == null) return null;
                        string line = JoinParts(parts);
                        return sec.Count > 1 ? $"{line}, {pos} of {sec.Count}" : line;
                    }
                    string rowLine = ComposeEntryRow(sec, index);
                    if (rowLine == null) return null;
                    return sec.Count > 1 ? $"{rowLine}, {pos} of {sec.Count}" : rowLine;
                }
            }
            catch { }
            return null;
        }

        /// <summary>An entry row: the rendered text, tags cleaned, the tab
        /// column-break spoken as a comma pause.</summary>
        private static string ComposeEntryRow(Section sec, int index)
        {
            try
            {
                var elements = Seams.UICanvas_getScrollableElements?.Invoke(sec.Canvas, null) as IList;
                if (elements == null || index < 0 || index >= elements.Count) return null;
                string raw = Seams.UITextBlock_content?.GetValue(elements[index]) as string;
                if (string.IsNullOrWhiteSpace(raw)) return null;
                string cleaned = Patches.TextCleaner.CleanText(raw.Replace("\t", ", "));
                if (string.IsNullOrWhiteSpace(cleaned)) return null;
                return cleaned;
            }
            catch { return null; }
        }

        /// <summary>Ability/spell grid rows compose entirely from the backing
        /// objects — the cells render icon art only. Spell rows follow the
        /// ruled §6.6 order: name → school, tier → cost → cascade → targets →
        /// prose; ability rows: name → (cost, time for maneuvers) → prose.
        /// The cost/cascade/targets lines are the game's own rendered lines
        /// (getFullDescription), with the 3-letter resource truncation
        /// expanded ("Att." → attunement).</summary>
        private static List<string> GridRowParts(Section sec, int index)
        {
            object backing = GridBackingRow(sec, index);
            if (backing == null) return null;

            string name = null;
            try { name = Seams.SkaldBaseObject_getName?.Invoke(backing, null) as string; } catch { }
            if (string.IsNullOrWhiteSpace(name)) return null;

            var parts = new List<string> { $"{name}." };

            string block = null;
            try { block = Seams.SkaldBaseObject_getFullDescription?.Invoke(backing, null) as string; } catch { }
            string cost = null, time = null, cascade = null, targets = null, prose = null;
            ParseComponentBlock(block, out cost, out time, out cascade, out targets, out prose);

            if (sec.Id == "spells")
            {
                string schoolTier = SpellSchoolTier(backing);
                if (schoolTier != null) parts.Add(schoolTier);
                if (cost != null) parts.Add(cost);
                if (cascade != null) parts.Add(cascade);
                if (targets != null) parts.Add(targets);
            }
            else if (sec.Id == "maneuvers")
            {
                if (cost != null) parts.Add(cost);
                if (time != null) parts.Add(time);
            }
            if (!string.IsNullOrWhiteSpace(prose)) parts.Add(prose);
            return parts;
        }

        private static object GridBackingRow(Section sec, int index)
        {
            try
            {
                object sheet = NativeScrollableList();
                if (sheet == null) return null;
                IList list = null;
                switch (sec.Id)
                {
                    case "spells":
                        object spellList = Seams.SpellBookSheet_spellList?.GetValue(sheet);
                        list = spellList == null ? null : Seams.SpellList_spells?.GetValue(spellList) as IList;
                        break;
                    case "maneuvers": list = Seams.AbilitySheet_maneuverList?.GetValue(sheet) as IList; break;
                    case "triggered": list = Seams.AbilitySheet_triggeredList?.GetValue(sheet) as IList; break;
                    case "passives": list = Seams.AbilitySheet_passiveList?.GetValue(sheet) as IList; break;
                }
                if (list == null || index < 0 || index >= list.Count) return null;
                return list[index];
            }
            catch { return null; }
        }

        /// <summary>"Fire, tier 1." — school display name via the game's own
        /// GameData.getAttributeName over getSchoolList()[0]; tier ruled IN
        /// (licensed by the tome-icon encoding + the Cascade formula).</summary>
        private static string SpellSchoolTier(object spell)
        {
            try
            {
                if (Seams.AbilitySpellType == null || !Seams.AbilitySpellType.IsInstanceOfType(spell)) return null;
                string school = null;
                var schools = Seams.AbilitySpell_getSchoolList?.Invoke(spell, null) as IList;
                if (schools != null && schools.Count > 0)
                {
                    string id = schools[0] as string;
                    if (id != null)
                        try { school = Seams.GameData_getAttributeName?.Invoke(null, new object[] { id }) as string; }
                        catch { }
                }
                string tier = null;
                try { tier = Seams.AbilitySpell_getTier?.Invoke(spell, null)?.ToString(); } catch { }
                if (school != null && tier != null) return $"{school}, tier {tier}.";
                if (school != null) return $"{school}.";
                if (tier != null) return $"Tier {tier}.";
            }
            catch { }
            return null;
        }

        /// <summary>Line-parse the game's own composed block: [type] line
        /// skipped (the section already attributes it), the labeled lines
        /// harvested, everything after the first blank = prose.</summary>
        private static void ParseComponentBlock(string block, out string cost, out string time,
            out string cascade, out string targets, out string prose)
        {
            cost = null; time = null; cascade = null; targets = null; prose = null;
            if (string.IsNullOrWhiteSpace(block)) return;
            var proseLines = new List<string>();
            bool inProse = false;
            foreach (var rawLine in block.Split('\n'))
            {
                string line = Patches.TextCleaner.CleanText(rawLine).Trim();
                if (line.Length == 0) { if (cost != null || time != null || cascade != null || targets != null || proseLines.Count > 0) inProse = true; continue; }
                if (!inProse)
                {
                    if (line.StartsWith("[") && line.EndsWith("]")) continue; // the type line — section attributes it
                    if (line.StartsWith("Cost:", StringComparison.OrdinalIgnoreCase))
                    { cost = ExpandResource(line.Replace("Cost:", "Cost").Trim()); continue; }
                    if (line.StartsWith("Cascade:", StringComparison.OrdinalIgnoreCase))
                    { cascade = EnsureDot(line.Replace("Cascade:", "Cascade").Trim()); continue; }
                    if (line.StartsWith("Time Use:", StringComparison.OrdinalIgnoreCase))
                    { time = EnsureDot(line.Replace("Time Use:", "Time use").Trim()); continue; }
                    if (line.StartsWith("Targets:", StringComparison.OrdinalIgnoreCase)
                        || line.StartsWith("Target:", StringComparison.OrdinalIgnoreCase))
                    { targets = EnsureDot(line); continue; }
                }
                inProse = true;
                proseLines.Add(line);
            }
            if (proseLines.Count > 0) prose = EnsureDot(string.Join(" ", proseLines));
        }

        /// <summary>"Cost 2 Att." → "Cost 2 attunement." — the game's own
        /// 3-letter resource truncation (AbilityUseable.printCost) expanded
        /// so no reader speaks "att dot".</summary>
        private static string ExpandResource(string costLine)
        {
            string s = costLine
                .Replace(" Att.", " attunement.")
                .Replace(" Sta.", " stamina.")
                .Replace(" Vit.", " vitality.");
            return EnsureDot(s);
        }

        private static string EnsureDot(string s)
            => string.IsNullOrWhiteSpace(s) ? s : (s.EndsWith(".") ? s : s + ".");

        private static string JoinParts(List<string> parts) => string.Join(" ", parts);

        // =====================================================================
        // Plumbing
        // =====================================================================

        /// <summary>The canvas the game would serve right now (redirect
        /// included) — the funnel park target and gate-B facet source.</summary>
        private static object CurrentCanvasVirtual()
        {
            if (Seams.GUIControl_getControllerScrollableList == null || _gui == null) return null;
            try { return Seams.GUIControl_getControllerScrollableList.Invoke(_gui, null); }
            catch { return null; }
        }

        private static int ScrollableCount(object canvas)
        {
            if (canvas == null) return 0;
            try
            {
                var elements = Seams.UICanvas_getScrollableElements?.Invoke(canvas, null) as IList;
                return elements?.Count ?? 0;
            }
            catch { return 0; }
        }

        private static bool IsEmptyPseudoRow(object canvas, int index)
        {
            try
            {
                var elements = Seams.UICanvas_getScrollableElements?.Invoke(canvas, null) as IList;
                if (elements == null || index < 0 || index >= elements.Count) return false;
                string raw = Seams.UITextBlock_content?.GetValue(elements[index]) as string;
                if (raw == null) return false;
                string cleaned = Patches.TextCleaner.CleanText(raw).Trim();
                return cleaned.Equals("-Empty-", StringComparison.OrdinalIgnoreCase)
                    || cleaned.Equals("--Empty--", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>Harvest the section's rendered header text verbatim
        /// (game-sanctioned labels).</summary>
        private static string HarvestHeader(object canvas)
        {
            try
            {
                var elements = Seams.UICanvas_getScrollableElements?.Invoke(canvas, null) as IList;
                if (elements == null || elements.Count == 0) return null;
                string raw = Seams.UITextBlock_content?.GetValue(elements[0]) as string;
                if (string.IsNullOrWhiteSpace(raw)) return null;
                string cleaned = Patches.TextCleaner.CleanText(raw).Trim();
                return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
            }
            catch { return null; }
        }

        /// <summary>Virtual-mouse park. Sheet rows use the native snap's own
        /// offsets (GUIControl.cs:1845, (2,-6) — entry1 is 78px wide with
        /// centered text); grids and gate-B lists keep the proven (8,-8).</summary>
        private static void Park(Section sec)
        {
            if (sec?.Canvas == null) return;
            try
            {
                bool sheetRow = sec.IndexRows && !sec.IsGrid;
                int dx = sheetRow ? 2 : 8, dy = sheetRow ? -6 : -8;
                Seams.GUIControl_setMouseToUIElement?.Invoke(_gui, new object[] { sec.Canvas, dx, dy });
            }
            catch { }
        }
    }
}

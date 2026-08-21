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
    /// facet layer, and bespoke row composition. Gate D: the inventory
    /// family (inventory / container / trade) — grid sections walk the live
    /// filtered item list by direct seat + park (the native funnel is
    /// column-major over a row-major list), R11 full-composition rows via
    /// ItemRowComposer, the game's own scroll window driven with a mod-side
    /// clamp, worn slots, trade offer facets, gold-riding censuses; speech
    /// rides the hover join (one voice for table steps and physical mouse);
    /// InventoryHoverPatch composes through the table, WornZonePatch and
    /// InventorySegmentPatch stand down while owned.
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
            // Gate F (§6.16): the two clean creation lists — real paged
            // ListButtonControls on the gate-B shape.
            { "CharacterCreationClassState",      "Classes" },
            { "CharacterCreationBackgroundState", "Backgrounds" },
        };

        // Gate F: party management (§6.13 — the one RESCUE: no funnel driver
        // exists on the state and its native scrollable list is a phantom
        // empty ListButtonControl; the table supplies 100% of the
        // navigation) and the difficulty selector (§6.16 — the native funnel
        // serves only the horizontal difficulty row; the settings list below
        // has NO native cursor).
        private const string PartyStateName = "PartyManagementState";
        private const string DifficultyStateName = "DifficultySelectorState";
        private const string ButtonsLabel = "Buttons";

        // Gate E: the settings family — gate-B funnel screens with four
        // settings-specific rules (survey 2026-08-21): the section label
        // harvests the game's own list name ("Gameplay Settings"…); A/D
        // stays the game's plus/minus chooser (driven through the native
        // sideways so the shipped "Plus."/"Minus." join speaks); the funnel
        // edge is refused mod-side on non-pageable lists (the native
        // increment over-runs any list shorter than its page); the whole
        // table goes INERT while the key-binding capture is live (the
        // capture reads SkaldIO's raw poll — the swallow choke never sees
        // it). Exact class names — two lack the State suffix.
        private static readonly HashSet<string> SettingsScreens = new HashSet<string>
        {
            "SettingsGameplayState", "SettingsDifficulty", "SettingsVideo",
            "SettingsAudioState", "SettingsFontSelectionState", "SettingsKeyBindingState",
            // Gate F (§6.16): the appearance editor is architecturally a
            // settings screen — the same UITextSliderControl surface, the
            // same A/D-stays-the-chooser rule (the game renders labelled
            // ordinals "Portrait 3"; spoken verbatim, hidden names stay
            // unspoken). Note the game's own misspelling.
            "CharacterCreationApperanceState",
        };

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

        // Gate D: the inventory family — grid sections walk the live filtered
        // item list by DIRECT SEAT + park (the native funnel is column-major
        // over a row-major list, survey 2026-08-21); the table drives the
        // game's own scroll window with a mod-side clamp. Worn = 12 fixed
        // slot rows. Buttons/Services ride the redirect + index seat.
        private enum InvKind { Grid, Worn, Services, Buttons }

        private sealed class InvSectionDef
        {
            public string Id;
            public string Label;
            public int Column;              // 0 left, 1 right, -1 shared tail
            public InvKind Kind;
        }

        private static readonly Dictionary<string, InvSectionDef[]> InvScreens
            = new Dictionary<string, InvSectionDef[]>
        {
            // §6.8: W/S = Items Worn ↔ Party Inventory ↔ Buttons (single column).
            { "InventoryGridState", new[]
                {
                    new InvSectionDef { Id = "worn", Label = "Items Worn", Column = 0, Kind = InvKind.Worn },
                    new InvSectionDef { Id = "main", Label = "Party Inventory", Column = 0, Kind = InvKind.Grid },
                    new InvSectionDef { Id = "buttons", Label = ButtonsLabel, Column = -1, Kind = InvKind.Buttons },
                } },
            // §6.9: A/D = Party Inventory ↔ Container; Buttons = shared tail.
            { "ContainerState", new[]
                {
                    new InvSectionDef { Id = "main", Label = "Party Inventory", Column = 0, Kind = InvKind.Grid },
                    new InvSectionDef { Id = "secondary", Label = "Container", Column = 1, Kind = InvKind.Grid },
                    new InvSectionDef { Id = "buttons", Label = ButtonsLabel, Column = -1, Kind = InvKind.Buttons },
                } },
            // §6.10 (corrected geometry, gate-D survey): A/D = Party Inventory
            // ↔ Merchant; right chain: Merchant ↔ Services ↔ Buttons.
            { "TradeState", new[]
                {
                    new InvSectionDef { Id = "main", Label = "Party Inventory", Column = 0, Kind = InvKind.Grid },
                    new InvSectionDef { Id = "secondary", Label = "Merchant", Column = 1, Kind = InvKind.Grid },
                    new InvSectionDef { Id = "services", Label = "Services", Column = 1, Kind = InvKind.Services },
                    new InvSectionDef { Id = "buttons", Label = ButtonsLabel, Column = -1, Kind = InvKind.Buttons },
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
        private static InvSectionDef[] _invDef;  // gate-D screen
        private static bool _settingsScreen;     // gate-E screen (simple + settings rules)
        private static bool _partyScreen;        // gate-F: party management (bespoke portraits)
        private static bool _difficultyScreen;   // gate-F: difficulty selector (bespoke)
        private static bool _registered;         // any registry matched this frame

        // Gate-D mod-side memory: the current section id (grid sections have
        // no redirect canvas to infer from), the per-section row anchor (flat
        // item index / worn slot), and the anchored item for identity
        // re-anchor across filter changes and re-sorts.
        private static string _invCurrentId;
        private static readonly Dictionary<string, int> _invAnchor = new Dictionary<string, int>();
        private static readonly Dictionary<string, object> _invAnchorItem = new Dictionary<string, object>();

        private static object _redirectCanvas;   // current non-native section canvas
        private static bool _resolving;          // re-entrancy guard for NativeScrollableList
        private static int _facet;               // parked facet (0 = the row line)
        private static readonly string[] _colRemember = new string[2];
        private static int _armedLogFrame = -1;

        private static bool Refresh()
        {
            if (Time.frameCount == _frame) return _registered;
            _frame = Time.frameCount;
            _state = null; _gui = null; _simpleLabel = null; _sheetDef = null; _invDef = null;
            _settingsScreen = false; _partyScreen = false; _difficultyScreen = false;
            _registered = false;
            if (_enabled == null || !_enabled.Value) return false;
            try
            {
                object s = Pump.CurrentStateObject();
                if (s == null) return false;
                string name = s.GetType().Name;
                bool simple = SimpleScreens.TryGetValue(name, out string label);
                if (!simple && SettingsScreens.Contains(name))
                {
                    simple = true;
                    _settingsScreen = true;
                    label = SettingsLabelOf(s);
                }
                bool sheet = !simple && SheetScreens.TryGetValue(name, out _sheetDef);
                bool inv = !simple && !sheet && InvScreens.TryGetValue(name, out _invDef);
                bool party = !simple && !sheet && !inv && name == PartyStateName;
                bool difficulty = !simple && !sheet && !inv && !party && name == DifficultyStateName;
                if (!simple && !sheet && !inv && !party && !difficulty)
                { _sheetDef = null; _invDef = null; return false; }
                object gui = Seams.StateBase_guiControl?.GetValue(s);
                if (gui == null) { _sheetDef = null; _invDef = null; return false; }
                _state = s; _gui = gui;
                if (simple) _simpleLabel = label;
                _partyScreen = party;
                _difficultyScreen = difficulty;
                _registered = true;
                return true;
            }
            catch { _sheetDef = null; _invDef = null; return false; }
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
            if (_settingsScreen && RebindCaptureActive()) return false;
            return true;
        }

        /// <summary>The key-binding capture (gate E): while `assigning` is
        /// true the game turns ANY key into a rebind by reading SkaldIO's
        /// raw poll — the swallow choke never sees it, so the table must go
        /// whole-inert on the game's own flag (no proxy; every table key
        /// would otherwise both move the cursor AND become the binding).</summary>
        private static bool RebindCaptureActive()
        {
            try
            {
                return Seams.SettingsKeyBindingStateType != null
                    && Seams.SettingsKeyBindingStateType.IsInstanceOfType(_state)
                    && Seams.SettingsKeyBinding_assigning != null
                    && (bool)Seams.SettingsKeyBinding_assigning.GetValue(_state);
            }
            catch { return false; }
        }

        /// <summary>"Gameplay Settings" / "Key Bindings" — harvested from the
        /// RENDERED sheet header, not the backing list (gate-E review
        /// MUST-FIX: the key-bindings list never names itself, so
        /// getListName() returns the "Components" ctor default while the game
        /// renders its own "Key Bindings" override on top). List-name
        /// fallback covers a not-yet-rendered first frame.</summary>
        private static string SettingsLabelOf(object state)
        {
            try
            {
                object gui = Seams.StateBase_guiControl?.GetValue(state);
                object complex = gui == null ? null
                    : Seams.GUIControl_sheetComplexField?.GetValue(gui);
                object header = complex == null ? null
                    : Seams.SheetComplex_header?.GetValue(complex);
                string rendered = header == null ? null
                    : Seams.UITextBlock_content?.GetValue(header) as string;
                if (!string.IsNullOrWhiteSpace(rendered))
                {
                    string cleaned = Patches.TextCleaner.CleanText(rendered).Trim();
                    if (cleaned.Length > 0) return cleaned;
                }
                object list = Seams.SettingsBase_list?.GetValue(state);
                string name = list == null ? null
                    : Seams.SkaldObjectList_getListName?.Invoke(list, null) as string;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    string cleaned = Patches.TextCleaner.CleanText(name).Trim();
                    if (cleaned.Length > 0) return cleaned;
                }
            }
            catch { }
            return "Settings";
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
            // Live-membership revalidation (adversarial review gate C
            // MUST-FIX): the abilities/spellbook sheets reallocate their
            // grids and entries on an in-place PC-cycle — same state class,
            // no transition event — and the orphaned canvas keeps its last
            // rendered rows, so a count check alone keeps serving the
            // PREVIOUS character's detached canvas to every native caller.
            var sections = ResolveSections();
            for (int i = 0; i < sections.Count; i++)
                if (ReferenceEquals(sections[i].Canvas, canvas))
                    return sections[i].Count > 0 ? canvas : null;
            _redirectCanvas = null;
            return null;
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
            _invCurrentId = null;
            _invAnchor.Clear();
            _invAnchorItem.Clear();
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
            public InvSectionDef InvDef; // gate-D only
            public string Id;
            public string Label;
            public object Canvas;       // gate-D grid sections: the SEGMENT
            public int Start;           // first walkable row (header skipped)
            public int Count;           // walkable rows
            public int Column;
            public bool IndexRows;
            public bool IsGrid;
            public bool PortraitRows;   // gate-F party management: park-driven portrait cells
        }

        private static int _secFrame = -1;
        private static List<Section> _sections;

        private static List<Section> ResolveSections()
        {
            if (Time.frameCount == _secFrame && _sections != null) return _sections;
            _secFrame = Time.frameCount;
            _sections = _sheetDef != null ? ResolveSheetSections()
                : _invDef != null ? ResolveInvSections()
                : _partyScreen ? ResolvePartySections()
                : _difficultyScreen ? ResolveDifficultySections()
                : ResolveSimpleSections();
            return _sections;
        }

        /// <summary>Gate F §6.13: the two portrait blocks off the state's own
        /// UI field (the sheet complex serves a phantom empty list — this
        /// screen has no native navigation at all; the table supplies it).
        /// Row counts are the LIVE party/bench lists — empty portrait slots
        /// render as background frames, never rows.</summary>
        private static List<Section> ResolvePartySections()
        {
            var sections = new List<Section>(3);
            try
            {
                object ui = Seams.PartyMgmt_ui?.GetValue(_state);
                if (ui == null) return sections;
                object dc = ItemRowComposer.DC();
                object party = dc == null ? null : Seams.DataControl_getParty?.Invoke(dc, null);
                object bench = dc == null ? null : Seams.DataControl_getSideBench?.Invoke(dc, null);
                int partyCount = MemberCount(party);
                int benchCount = MemberCount(bench);

                // Counts clamp to the RENDERED slots (6 / 2×6) — the bench
                // list has no native size cap, and a member past slot 12
                // would speak without a parkable cell (review SHOULD-FIX).
                object partyBlock = Seams.PartyUI_partyBlock?.GetValue(ui);
                if (partyBlock != null)
                    sections.Add(new Section { Id = "party", Label = "Main Party", Canvas = partyBlock,
                        Start = 0, Count = Math.Min(partyCount, 6), Column = 0, PortraitRows = true });
                object benchBlock = Seams.PartyUI_sideBenchBlock?.GetValue(ui);
                if (benchBlock != null)
                    sections.Add(new Section { Id = "bench", Label = "Camp Followers", Canvas = benchBlock,
                        Start = 0, Count = Math.Min(benchCount, 12), Column = 0, PortraitRows = true });
                object numeric = NumericButtons();
                int n = ScrollableCount(numeric);
                if (n > 0)
                    sections.Add(new Section { Id = "buttons", Label = ButtonsLabel, Canvas = numeric,
                        Start = 0, Count = n, Column = 0, IndexRows = true });
            }
            catch { }
            return sections;
        }

        private static int MemberCount(object skaldList)
        {
            try
            {
                var objects = skaldList == null ? null
                    : Seams.SkaldObjectList_getObjectList?.Invoke(skaldList, null) as IList;
                return objects?.Count ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>Gate F §6.16: the difficulty selector — the native
        /// scrollable list is the HORIZONTAL difficulty row (all four native
        /// directions alias onto it); the sub-settings list below is a real,
        /// fed ListButtonControl with no native cursor — the table gives it
        /// index-seat rows.</summary>
        private static List<Section> ResolveDifficultySections()
        {
            var sections = new List<Section>(3);
            object row = NativeScrollableList();
            int rowCount = ScrollableCount(row);
            if (row != null && rowCount > 0)
                sections.Add(new Section { Id = "primary", Label = "Difficulty", Canvas = row,
                    Start = 0, Count = rowCount, Column = 0, IndexRows = false });
            try
            {
                object list = Seams.GUIControl_listButtonsField?.GetValue(_gui);
                int n = ScrollableCount(list);
                if (list != null && n > 0)
                    sections.Add(new Section { Id = "settings", Label = "Settings", Canvas = list,
                        Start = 0, Count = n, Column = 0, IndexRows = true });
            }
            catch { }
            object numeric = NumericButtons();
            int bn = ScrollableCount(numeric);
            if (numeric != null && bn > 0)
                sections.Add(new Section { Id = "buttons", Label = ButtonsLabel, Canvas = numeric,
                    Start = 0, Count = bn, Column = 0, IndexRows = true });
            return sections;
        }

        /// <summary>Gate-D sections against the live inventory sheet. Grid
        /// sections carry the SEGMENT as their canvas and the live filtered
        /// item count as their row count; the worn section is the fixed 12
        /// slots; Services/Buttons are index-seated button canvases (the
        /// funnel is never used here — its edge fallthrough would slide the
        /// grid window from inside the Buttons section, survey 2026-08-21).</summary>
        private static List<Section> ResolveInvSections()
        {
            var sections = new List<Section>(_invDef.Length);
            object sheet = NativeScrollableList();
            if (sheet == null || Seams.UIInventorySheetBaseType == null
                || !Seams.UIInventorySheetBaseType.IsInstanceOfType(sheet)) return sections;

            foreach (var def in _invDef)
            {
                switch (def.Kind)
                {
                    case InvKind.Worn:
                    {
                        object worn = Seams.InvSheet_itemInteractionGrid?.GetValue(sheet);
                        if (worn == null || Seams.ItemsWornUIType == null
                            || !Seams.ItemsWornUIType.IsInstanceOfType(worn)) break;
                        sections.Add(new Section { InvDef = def, Id = def.Id, Label = def.Label,
                            Canvas = worn, Start = 0, Count = 12, Column = def.Column });
                        break;
                    }
                    case InvKind.Grid:
                    {
                        var field = def.Id == "main"
                            ? Seams.InvSheet_mainInventoryGrid : Seams.InvSheet_secondaryInventoryGrid;
                        object segment = field?.GetValue(sheet);
                        if (segment == null || Seams.InventorySegmentType == null
                            || !Seams.InventorySegmentType.IsInstanceOfType(segment)) break;
                        var items = FilteredItemsOf(segment);
                        sections.Add(new Section { InvDef = def, Id = def.Id, Label = def.Label,
                            Canvas = segment, Start = 0, Count = items?.Count ?? 0, Column = def.Column });
                        break;
                    }
                    case InvKind.Services:
                    {
                        if (Seams.UIInventorySheetMerchantType == null
                            || !Seams.UIInventorySheetMerchantType.IsInstanceOfType(sheet)) break;
                        object services = Seams.Merchant_serviceButtons?.GetValue(sheet);
                        if (services == null) break;
                        // Always a canvas; zero buttons when the store offers
                        // none — a zero-row census stop, never dropped (the
                        // strip exists in the render).
                        sections.Add(new Section { InvDef = def, Id = def.Id, Label = def.Label,
                            Canvas = services, Start = 0, Count = ScrollableCount(services),
                            Column = def.Column, IndexRows = true });
                        break;
                    }
                    case InvKind.Buttons:
                    {
                        object numeric = NumericButtons();
                        if (numeric == null) break;
                        int n = ScrollableCount(numeric);
                        if (n == 0) break;
                        sections.Add(new Section { InvDef = def, Id = def.Id, Label = def.Label,
                            Canvas = numeric, Start = 0, Count = n, Column = def.Column,
                            IndexRows = true });
                        break;
                    }
                }
            }
            return sections;
        }

        /// <summary>The live type-filtered list the segment renders and
        /// resolves clicks from — the game's own row↔item ground truth
        /// (UIInventorySheetBase.cs:113-159), read fresh every call, never
        /// cached (re-sorts, filter changes, transfers).</summary>
        private static IList FilteredItemsOf(object segment)
        {
            try
            {
                if (Seams.InvSegment_inventory == null || Seams.Inventory_getListByType == null) return null;
                object inventory = Seams.InvSegment_inventory.GetValue(segment);
                if (inventory == null) return null;
                object itemTypes = Seams.InvSegment_itemTypes?.GetValue(segment);
                return Seams.Inventory_getListByType.Invoke(inventory, new[] { itemTypes, (object)false })
                    as IList;
            }
            catch { return null; }
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
                    // Settings (gate E): index-seat the Buttons section — the
                    // funnel's edge fallthrough would slide the settings list
                    // window from inside Buttons (the gate-D hazard again).
                    sections.Add(new Section { Id = "buttons", Label = ButtonsLabel, Canvas = numeric,
                        Start = 0, Count = numericCount, Column = 0, IndexRows = _settingsScreen });
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
            // Gate-D screens track the current section by id (grid sections
            // are park-driven — no redirect canvas to infer from).
            if (_invDef != null && _invCurrentId != null)
            {
                for (int i = 0; i < sections.Count; i++)
                    if (sections[i].Id == _invCurrentId) return i;
                _invCurrentId = null;
            }
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

            if (sec.InvDef != null
                && (sec.InvDef.Kind == InvKind.Grid || sec.InvDef.Kind == InvKind.Worn))
            {
                InvRowStep(sections, cur, sec, upward);
                return;
            }

            if (sec.PortraitRows)
            {
                PartyRowStep(sections, cur, sec, upward);
                return;
            }

            if (!sec.IndexRows)
            {
                // Gate E: refuse the funnel edge on non-pageable settings
                // lists — the native increment is the LIST length, not the
                // overflow, so the game happily scrolls a 2-row list to show
                // 1 row (survey 2026-08-21). Genuinely pageable lists
                // (count > page) keep the native slide.
                if (_settingsScreen && SettingsFunnelEdgeRefused(sec, upward)) return;
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
                // First touch of this section: a full landing (label + census
                // ride the zone label, which also punches through the
                // selection dedup — review SHOULD-FIX: the bare seat could
                // land silent when native clamping had already spoken 0).
                LandSection(sec);
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
        /// in-section row moves — no census, no label. Gate-D: grid/worn
        /// sections adopt through the game's OWN surface field (the
        /// WornZonePatch precedent — no proxy) and never redirect the
        /// scrollable list; Services/Buttons redirect it (they are outside
        /// the sheet's native rotation entirely).</summary>
        private static void AdoptSection(Section sec)
        {
            if (_invDef != null)
            {
                _invCurrentId = sec.Id;
                bool served = sec.InvDef != null
                    && (sec.InvDef.Kind == InvKind.Services || sec.InvDef.Kind == InvKind.Buttons);
                _redirectCanvas = served ? sec.Canvas : null;
                if (sec.InvDef != null
                    && (sec.InvDef.Kind == InvKind.Grid || sec.InvDef.Kind == InvKind.Worn))
                {
                    try
                    {
                        object sheet = NativeScrollableList();
                        if (sheet != null && Seams.UIInventorySheetBaseType != null
                            && Seams.UIInventorySheetBaseType.IsInstanceOfType(sheet))
                            Seams.InvSheet_currentControllerSurface?.SetValue(sheet, sec.Canvas);
                    }
                    catch { }
                }
                if (sec.Column >= 0) _colRemember[sec.Column] = sec.Id;
                return;
            }
            object native = NativeScrollableList();
            _redirectCanvas = ReferenceEquals(sec.Canvas, native) ? null : sec.Canvas;
            if (sec.Column >= 0) _colRemember[sec.Column] = sec.Id;
        }

        // =====================================================================
        // Gate D: inventory-family rows (grid = live filtered item walk by
        // direct seat + window drive; worn = the 12 fixed slots)
        // =====================================================================

        private static ItemRowComposer.Mode ModeOf(Section sec)
        {
            if (_state == null || _state.GetType().Name != "TradeState") return ItemRowComposer.Mode.Plain;
            if (sec.Id == "main") return ItemRowComposer.Mode.TradeParty;
            if (sec.Id == "secondary") return ItemRowComposer.Mode.TradeMerchant;
            return ItemRowComposer.Mode.Plain;
        }

        private static object ItemAt(Section sec, int flatIdx)
        {
            var items = FilteredItemsOf(sec.Canvas);
            if (items == null || flatIdx < 0 || flatIdx >= items.Count) return null;
            return items[flatIdx];
        }

        private static List<string> InvRowParts(Section sec, int flatIdx)
        {
            if (sec.InvDef.Kind == InvKind.Worn) return WornRowParts(flatIdx);
            return ItemRowComposer.RowParts(ItemAt(sec, flatIdx), ModeOf(sec));
        }

        /// <summary>The full row line with the trailing positional counter
        /// (counts always trail — standing rule).</summary>
        private static string InvRowLine(Section sec, int flatIdx)
        {
            if (sec.InvDef.Kind == InvKind.Worn)
            {
                var wp = WornRowParts(flatIdx);
                return wp == null ? null : JoinParts(wp);
            }
            var parts = InvRowParts(sec, flatIdx);
            if (parts == null) return "Empty.";
            string line = JoinParts(parts);
            return sec.Count > 1 ? $"{line} {flatIdx + 1} of {sec.Count}." : line;
        }

        /// <summary>Worn slot row: "Melee: Waraxe." / "Ranged: empty." —
        /// slot labels from the game's own icon files, item from the same
        /// Character getter the renderer paints (Pump's shipped map). The
        /// item's R11 parts follow as the row's lateral facets.</summary>
        private static List<string> WornRowParts(int slot)
        {
            string label = Pump.WornSlotLabel(slot);
            if (label == null) return null;
            object item = WornItemAt(slot);
            if (item == null) return new List<string> { $"{label}: empty." };
            var parts = ItemRowComposer.RowParts(item, ItemRowComposer.Mode.Plain);
            string identity = parts != null && parts.Count > 0 ? parts[0] : null;
            var result = new List<string>
                { identity != null ? $"{label}: {identity}" : $"{label}: equipped." };
            if (parts != null && parts.Count > 1) result.AddRange(parts.GetRange(1, parts.Count - 1));
            return result;
        }

        private static object WornItemAt(int slot)
        {
            try
            {
                var getters = Seams.Character_wornGetters;
                object character = Patches.WornZonePatch.WornCharacter ?? ItemRowComposer.CurrentPC();
                if (getters == null || character == null || slot < 0 || slot >= getters.Length
                    || getters[slot] == null) return null;
                return getters[slot].Invoke(character, null);
            }
            catch { return null; }
        }

        private static void InvRowStep(List<Section> sections, int cur, Section sec, bool upward)
        {
            if (sec.InvDef.Kind == InvKind.Grid && sec.Count == 0)
            {
                Scaffold.SpeechService.Say($"{sec.Label}, none.", "Nav");
                return;
            }
            int idx = _invAnchor.TryGetValue(sec.Id, out int a) ? a : -1;
            if (idx < 0 || idx >= sec.Count)
            {
                // First touch (or the anchor died with a shrink): full landing.
                LandInvSection(sec);
                return;
            }
            int next = idx + (upward ? -1 : +1);
            if (next < 0)
            {
                // Row flow (§3): cross the seam, announced.
                if (cur > 0) LandSection(sections[cur - 1], atLastRow: true);
                else Scaffold.SpeechService.Say("Top of list.", "Nav");
                return;
            }
            if (next >= sec.Count)
            {
                if (cur < sections.Count - 1) LandSection(sections[cur + 1], atFirstRow: true);
                else Scaffold.SpeechService.Say("Bottom of list.", "Nav");
                return;
            }
            AdoptSection(sec);
            SeatInvRow(sec, next, speak: true, queued: false);
        }

        /// <summary>Seat the walk on a flat row: drive the game's own window
        /// if needed, write the segment's column (the hover re-derive syncs
        /// it anyway — belt and braces), park the virtual mouse on the cell,
        /// align the hover records (our park must not echo through the hover
        /// join), and speak. A parked facet >0 speaks "identity, facet"
        /// instead of the full row (R11's scan-one-facet idiom).</summary>
        private static void SeatInvRow(Section sec, int flatIdx, bool speak, bool queued,
            string censusPrefix = null)
        {
            Pump.NotePlayerNav();
            if (sec.InvDef.Kind == InvKind.Worn)
            {
                int w = 6;
                int row = flatIdx / w, col = flatIdx % w;
                object worn = sec.Canvas;
                object grid = Seams.ItemsWornUI_grid?.GetValue(worn);
                ParkGridCell(grid, row, col);
                _invAnchor[sec.Id] = flatIdx;
                _invAnchorItem[sec.Id] = WornItemAt(flatIdx);
            }
            else
            {
                object seg = sec.Canvas;
                int w = GridWidth(seg);
                EnsureItemVisible(sec, flatIdx);
                int offset = OffsetOf(seg);
                int vis = flatIdx - offset;
                int row = w > 0 ? vis / w : 0, col = w > 0 ? vis % w : 0;
                try
                {
                    if (Seams.InvSegment_column != null && w > 0)
                        Seams.InvSegment_column.SetValue(seg, Math.Max(0, Math.Min(w - 1, col)));
                }
                catch { }
                object grid = Seams.InvSegment_grid?.GetValue(seg);
                ParkGridCell(grid, row, col);
                Pump.AlignInvHover(seg, row, col);
                _invAnchor[sec.Id] = flatIdx;
                _invAnchorItem[sec.Id] = ItemAt(sec, flatIdx);
            }

            if (!speak) return;
            string text;
            var parts = InvRowParts(sec, flatIdx);
            if (_facet > 0 && parts != null && parts.Count > 0)
            {
                // Parked facet (R11): rows speak "identity, facet" — facet f
                // maps to parts[f-1], matching InvFacetStep.
                int f = Math.Min(_facet, parts.Count);
                text = f <= 1 ? parts[0] : $"{parts[0]} {parts[f - 1]}";
            }
            else text = InvRowLine(sec, flatIdx) ?? "Empty.";
            if (censusPrefix != null) text = $"{censusPrefix} {text}";
            if (queued) Scaffold.SpeechService.SayQueued(text, "Nav");
            else Scaffold.SpeechService.Say(text, "Nav");
        }

        /// <summary>Grid/worn section landing: census + the restored row in
        /// ONE utterance, spoken directly (these sections have no selection
        /// write to carry a zone label — the label rides the line itself,
        /// F2-safe by construction).</summary>
        private static void LandInvSection(Section sec, bool atLastRow = false,
            bool atFirstRow = false, bool queued = false, int? explicitRow = null)
        {
            AdoptSection(sec);
            _facet = 0;

            string census = InvCensusOf(sec);
            if (sec.InvDef.Kind == InvKind.Grid && sec.Count == 0)
            {
                Pump.NotePlayerNav();
                if (queued) Scaffold.SpeechService.SayQueued($"{census}.", "Nav");
                else Scaffold.SpeechService.Say($"{census}.", "Nav");
                Scaffold.Log.Debug("Gate", $"table section land: {sec.Label} count=0");
                return;
            }

            int row;
            if (explicitRow.HasValue) row = explicitRow.Value;
            else if (atFirstRow) row = 0;
            else if (atLastRow) row = sec.Count - 1;
            else row = _invAnchor.TryGetValue(sec.Id, out int a) && a >= 0 && a < sec.Count ? a : 0;
            if (row < 0) row = 0;
            if (row >= sec.Count) row = sec.Count - 1;

            SeatInvRow(sec, row, speak: true, queued: queued, censusPrefix: $"{census}.");
            Scaffold.Log.Debug("Gate", $"table section land: {sec.Label} count={sec.Count} row={row}");
        }

        /// <summary>"Party Inventory, 5 items" (+ trade gold riding the
        /// census per the §6.10 ruling) / "Items Worn, 12" / "…, none".</summary>
        private static string InvCensusOf(Section sec)
        {
            if (sec.InvDef.Kind == InvKind.Worn) return $"{sec.Label}, 12";
            if (sec.Count == 0) return $"{sec.Label}, none{TradeGoldSuffix(sec)}";
            string items = sec.Count == 1 ? "1 item" : $"{sec.Count} items";
            return $"{sec.Label}, {items}{TradeGoldSuffix(sec)}";
        }

        private static string TradeGoldSuffix(Section sec)
        {
            try
            {
                if (_state == null || _state.GetType().Name != "TradeState"
                    || Seams.Inventory_getMoney == null) return "";
                if (sec.Id == "main")
                {
                    object inv = Seams.InvSegment_inventory?.GetValue(sec.Canvas);
                    if (inv == null) return "";
                    int gold = (int)Seams.Inventory_getMoney.Invoke(inv, null);
                    return $", {gold} gold";
                }
                if (sec.Id == "secondary")
                {
                    object store = ItemRowComposer.CurrentStore();
                    object inv = store == null ? null : Seams.Store_getInventory?.Invoke(store, null);
                    if (inv == null) return "";
                    int gold = (int)Seams.Inventory_getMoney.Invoke(inv, null);
                    return $", vendor gold {gold}";
                }
            }
            catch { }
            return "";
        }

        // ---- window drive (the game's own scrollbar, mod-side clamp) ----

        private static int GridWidth(object segment)
        {
            try { return (int)Seams.InvSegment_gridWidth.GetValue(segment); }
            catch { return 0; }
        }

        private static int OffsetOf(object segment)
        {
            try { return (int)Seams.InvSegment_offsetIndex.GetValue(segment); }
            catch { return 0; }
        }

        /// <summary>Slide the game's own window so the flat index is visible.
        /// The mod owns the clamp: the native increment derives from list
        /// length, not overflow, so the game itself can scroll a fully-visible
        /// list to a blank grid (survey 2026-08-21). Never called from inside
        /// UIScrollbar.updateMouseInteraction (the reclaim backstop's capture
        /// window) — this runs from the input path.</summary>
        private static void EnsureItemVisible(Section sec, int flatIdx)
        {
            try
            {
                object seg = sec.Canvas;
                int w = GridWidth(seg);
                if (w <= 0) return;
                object grid = Seams.InvSegment_grid?.GetValue(seg);
                int h = 0;
                if (grid != null && Seams.UIGridBase_height != null)
                    h = (int)Seams.UIGridBase_height.GetValue(grid);
                if (h <= 0) return;
                int offset = OffsetOf(seg);
                if (flatIdx >= offset && flatIdx < offset + w * h) return;

                int targetRow = flatIdx / w;
                int newOffRow = flatIdx < offset ? targetRow : targetRow - (h - 1);
                int maxOffRow = Math.Max(0, (sec.Count + w - 1) / w - h);
                if (newOffRow < 0) newOffRow = 0;
                if (newOffRow > maxOffRow) newOffRow = maxOffRow;

                Seams.InvSegment_offsetIndex?.SetValue(seg, newOffRow * w);
                object bar = Seams.InvSegment_scrollBar?.GetValue(seg);
                if (bar != null && Seams.UIScrollbar_degree != null && Seams.UIScrollbar_increment != null)
                {
                    int inc = System.Convert.ToInt32(Seams.UIScrollbar_increment.GetValue(bar));
                    float degree = inc <= 0 ? 0f : (float)newOffRow / inc;
                    if (degree < 0f) degree = 0f;
                    if (degree > 1f) degree = 1f;
                    Seams.UIScrollbar_degree.SetValue(bar, degree);
                }
            }
            catch { }
        }

        // =====================================================================
        // Gate F §6.13: party-management portrait rows (park-driven — this
        // screen has NO native navigation; rows are the LIVE member lists,
        // empty slots skipped by construction)
        // =====================================================================

        private static void PartyRowStep(List<Section> sections, int cur, Section sec, bool upward)
        {
            if (sec.Count == 0)
            {
                Scaffold.SpeechService.Say($"{sec.Label}, none.", "Nav");
                return;
            }
            int idx = _invAnchor.TryGetValue(sec.Id, out int a) ? a : -1;
            if (idx < 0 || idx >= sec.Count) { LandPartySection(sec); return; }
            int next = idx + (upward ? -1 : +1);
            if (next < 0)
            {
                if (cur > 0) LandSection(sections[cur - 1], atLastRow: true);
                else Scaffold.SpeechService.Say("Top of list.", "Nav");
                return;
            }
            if (next >= sec.Count)
            {
                if (cur < sections.Count - 1) LandSection(sections[cur + 1], atFirstRow: true);
                else Scaffold.SpeechService.Say("Bottom of list.", "Nav");
                return;
            }
            AdoptSection(sec);
            SeatPartyRow(sec, next, speak: true, censusPrefix: null);
        }

        private static void LandPartySection(Section sec, bool atLastRow = false, bool atFirstRow = false)
        {
            AdoptSection(sec);
            _facet = 0;
            string census = sec.Count == 0 ? $"{sec.Label}, none" : $"{sec.Label}, {sec.Count}";
            if (sec.Count == 0)
            {
                Pump.NotePlayerNav();
                Scaffold.SpeechService.Say($"{census}.", "Nav");
                Scaffold.Log.Debug("Gate", $"table section land: {sec.Label} count=0");
                return;
            }
            int row = atFirstRow ? 0
                : atLastRow ? sec.Count - 1
                : _invAnchor.TryGetValue(sec.Id, out int a) && a >= 0 && a < sec.Count ? a : 0;
            SeatPartyRow(sec, row, speak: true, censusPrefix: $"{census}.");
            Scaffold.Log.Debug("Gate", $"table section land: {sec.Label} count={sec.Count} row={row}");
        }

        /// <summary>Park on the portrait cell (6 per row) + speak the ruled
        /// vitals row: "Kat, 17 vitality, 2 wounds. 2 of 5." The click stays
        /// native (Z = the immediate move; refusals are game popups).</summary>
        private static void SeatPartyRow(Section sec, int slot, bool speak, string censusPrefix)
        {
            Pump.NotePlayerNav();
            ParkGridCell(sec.Canvas, slot / 6, slot % 6);
            _invAnchor[sec.Id] = slot;
            if (!speak) return;
            string text = ComposePartyRow(sec, slot) ?? "Empty.";
            if (censusPrefix != null) text = $"{censusPrefix} {text}";
            Scaffold.SpeechService.Say(text, "Nav");
        }

        private static object PartyMemberAt(Section sec, int slot)
        {
            try
            {
                object dc = ItemRowComposer.DC();
                if (dc == null) return null;
                object list = sec.Id == "party"
                    ? Seams.DataControl_getParty?.Invoke(dc, null)
                    : Seams.DataControl_getSideBench?.Invoke(dc, null);
                var objects = list == null ? null
                    : Seams.SkaldObjectList_getObjectList?.Invoke(list, null) as IList;
                if (objects == null || slot < 0 || slot >= objects.Count) return null;
                return objects[slot];
            }
            catch { return null; }
        }

        private static string ComposePartyRow(Section sec, int slot)
        {
            try
            {
                object member = PartyMemberAt(sec, slot);
                if (member == null) return null;
                string name = Seams.SkaldBaseObject_getName?.Invoke(member, null) as string;
                if (string.IsNullOrWhiteSpace(name)) return null;
                name = Patches.TextCleaner.CleanText(name).Trim();
                var bits = new List<string> { name };
                try
                {
                    if (Seams.Character_getVitality != null)
                        bits.Add($"{(int)Seams.Character_getVitality.Invoke(member, null)} vitality");
                }
                catch { }
                try
                {
                    if (Seams.Character_getWounds != null)
                    {
                        int w = (int)Seams.Character_getWounds.Invoke(member, null);
                        if (w > 0) bits.Add(w == 1 ? "1 wound" : $"{w} wounds");
                    }
                }
                catch { }
                string line = string.Join(", ", bits) + ".";
                return sec.Count > 1 ? $"{line} {slot + 1} of {sec.Count}." : line;
            }
            catch { return null; }
        }

        /// <summary>Gate E: true (and spoken) when a funnel step on a
        /// settings list would fall off a non-pageable list's edge — the
        /// native scroll would slide a fully-visible list off screen.</summary>
        private static bool SettingsFunnelEdgeRefused(Section sec, bool upward)
        {
            try
            {
                object list = Seams.SettingsBase_list?.GetValue(_state);
                if (list == null || Seams.SkaldObjectList_getCount == null
                    || Seams.SkaldObjectList_getMaxPageSize == null) return false;
                int count = (int)Seams.SkaldObjectList_getCount.Invoke(list, null);
                int page = (int)Seams.SkaldObjectList_getMaxPageSize.Invoke(list, null);
                if (count > page) return false; // genuinely pageable — native slide is correct
                int idx = CurrentIndex(sec.Canvas);
                int visible = ScrollableCount(sec.Canvas);
                if (!upward && idx >= visible - 1)
                { Scaffold.SpeechService.Say("Bottom of list.", "Nav"); return true; }
                if (upward && idx <= 0)
                { Scaffold.SpeechService.Say("Top of list.", "Nav"); return true; }
            }
            catch { }
            return false;
        }

        private static void ResetWindow(Section sec)
        {
            try
            {
                object seg = sec.Canvas;
                Seams.InvSegment_offsetIndex?.SetValue(seg, 0);
                object bar = Seams.InvSegment_scrollBar?.GetValue(seg);
                if (bar != null && Seams.UIScrollbar_degree != null)
                    Seams.UIScrollbar_degree.SetValue(bar, 0f);
            }
            catch { }
        }

        /// <summary>Park the virtual mouse on a grid cell. Cell widgets are
        /// ctor-created with fixed geometry (UIGridBase.cs:192-202), so the
        /// park is correct even on the frame the window slides. Offsets =
        /// the inventory sheet's own snap (GUIControlInventoryBase, (8,-8)).
        ///
        /// MECHANISM (gate-F review MUST-FIX, retroactively repairing the
        /// gate-D parks too): setMouseToUIElement parks at the passed
        /// CANVAS's own currently-selected CHILD (GUIControl.cs:1849-1858) —
        /// never at a passed leaf. A leaf UIPortrait isn't even a UICanvas
        /// (invoke threw); a leaf grid button no-opped (no child elements).
        /// The native contract, used by every game call site: seat the row
        /// canvas's own index, park the row canvas — the drill lands on the
        /// cell.</summary>
        private static void ParkGridCell(object grid, int row, int col)
        {
            try
            {
                if (grid == null) return;
                var rows = Seams.UICanvas_getScrollableElements?.Invoke(grid, null) as IList;
                if (rows == null || row < 0 || row >= rows.Count) return;
                object rowCanvas = rows[row];
                var cells = Seams.UICanvas_getScrollableElements?.Invoke(rowCanvas, null) as IList;
                if (cells == null || col < 0 || col >= cells.Count) return;
                try { Seams.UICanvas_setCurrentSelectedButton?.Invoke(rowCanvas, new object[] { col }); }
                catch { }
                Seams.GUIControl_setMouseToUIElement?.Invoke(_gui, new object[] { rowCanvas, 8, -8 });
            }
            catch { }
        }

        /// <summary>Left/Right on inventory rows: facet 0 = the full row
        /// line; 1.. = the row's parts (identity, offer, type, stats,
        /// value/weight, prose). The parked facet persists across rows
        /// (R11: park on gold → rows speak "name, gold").</summary>
        private static void InvFacetStep(Section sec, int direction)
        {
            int idx = _invAnchor.TryGetValue(sec.Id, out int a) ? a : -1;
            if (idx < 0 || idx >= sec.Count)
            {
                Scaffold.SpeechService.Say(
                    sec.InvDef.Kind == InvKind.Grid && sec.Count == 0
                        ? $"{sec.Label}, none." : "No columns.", "Nav");
                return;
            }
            var parts = InvRowParts(sec, idx);
            if (parts == null || parts.Count == 0)
            { Scaffold.SpeechService.Say("No columns.", "Nav"); return; }

            int max = parts.Count; // facet 0 = row line, 1..Count = parts
            _facet += direction;
            if (_facet < 0) _facet = 0;
            if (_facet > max) _facet = max;
            string text = _facet == 0 ? (InvRowLine(sec, idx) ?? "Empty.") : parts[_facet - 1];
            Scaffold.SpeechService.Say(string.IsNullOrWhiteSpace(text) ? "No columns." : text, "Nav");
        }

        /// <summary>The hover join's composer while the table owns an
        /// inventory-family state: physical-mouse hover and any native snap
        /// speak the same R11 row the table's own steps speak, and the hover
        /// truth adopts the section + anchor (one voice, one position).</summary>
        internal static string ComposeInvHoverCell(object segment, int row, int col)
        {
            try
            {
                if (!Active() || _invDef == null) return null;
                var sections = ResolveSections();
                foreach (var sec in sections)
                {
                    if (sec.InvDef?.Kind != InvKind.Grid || !ReferenceEquals(sec.Canvas, segment)) continue;
                    int w = GridWidth(segment);
                    if (w <= 0) return null;
                    int flat = OffsetOf(segment) + row * w + col;
                    if (flat < 0 || flat >= sec.Count) return "Empty.";
                    _invCurrentId = sec.Id;
                    _invAnchor[sec.Id] = flat;
                    _invAnchorItem[sec.Id] = ItemAt(sec, flat);
                    return InvRowLine(sec, flat);
                }
            }
            catch { }
            return null;
        }

        /// <summary>The section label for a hovered segment while owned —
        /// the physical-mouse grid-crossing announcement (rides the hover
        /// join's zone prefix, same slot crafting/camp use).</summary>
        internal static string SectionLabelFor(object segment)
        {
            try
            {
                if (!Active() || _invDef == null) return null;
                var sections = ResolveSections();
                foreach (var sec in sections)
                    if (ReferenceEquals(sec.Canvas, segment)) return sec.Label;
            }
            catch { }
            return null;
        }

        /// <summary>R12 on the filter choke: reset the window (the native
        /// degree persists while the increment changes — the window would
        /// silently teleport), re-anchor by item identity, and land with the
        /// naming census QUEUED behind the game's own filter line.</summary>
        internal static void OnFilterChanged()
        {
            try
            {
                if (_invDef == null || !Active()) return;
                var sections = ResolveSections();
                if (sections.Count == 0) return;
                var sec = sections[CurrentSectionIndex(sections)];
                if (sec.InvDef?.Kind != InvKind.Grid) return;
                ResetWindow(sec);
                int target = 0;
                if (_invAnchorItem.TryGetValue(sec.Id, out object anchored) && anchored != null)
                {
                    var items = FilteredItemsOf(sec.Canvas);
                    if (items != null)
                        for (int i = 0; i < items.Count; i++)
                            if (ReferenceEquals(items[i], anchored)) { target = i; break; }
                }
                LandInvSection(sec, queued: true, explicitRow: target);
            }
            catch { }
        }

        /// <summary>Capture-not-speak for the sheet panel while the table
        /// owns an inventory-family state (gate-D ruling: the R11 row already
        /// carries the comparative block; the panel stays on the reading
        /// plane).</summary>
        internal static bool SuppressSheetPanelSpeech()
        {
            return _invDef != null && Active();
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

            if (_sheetDef == null && _invDef == null)
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
            // Gate E (ruling R1a): A/D on settings stays the game's own
            // plus/minus chooser — the table drives the native sideways so
            // the shipped ArrowFlipJoin speaks "Plus."/"Minus." unchanged.
            // (Settings are single-column; the section axis is empty here.)
            if (_settingsScreen)
            {
                var sections0 = ResolveSections();
                var secNow = sections0.Count > 0 ? sections0[CurrentSectionIndex(sections0)] : null;
                // The flip exists only where the canvas IS a slider control —
                // on the key-bindings tab the sideways chain dead-ends in
                // UICanvas's no-op (gate-E review MUST-FIX: silence, not a
                // refusal). Buttons refuse likewise.
                bool sliderCanvas = secNow != null && secNow.Id != "buttons"
                    && Seams.UITextSliderControlType != null
                    && Seams.UITextSliderControlType.IsInstanceOfType(secNow.Canvas);
                if (!sliderCanvas)
                {
                    Scaffold.SpeechService.Say(direction < 0 ? "No section left." : "No section right.", "Nav");
                    return;
                }
                var sideways = direction < 0
                    ? Seams.GUIControl_controllerScrollSidewaysLeft
                    : Seams.GUIControl_controllerScrollSidewaysRight;
                if (sideways != null)
                {
                    Pump.NotePlayerNav();
                    try { sideways.Invoke(_gui, null); } catch { }
                    return;
                }
            }
            // Gate F: the difficulty selector's horizontal row — A/D steps
            // its options through the native sideways (the game aliases all
            // four directions onto this row; the alias is the affordance).
            if (_difficultyScreen)
            {
                var dSections = ResolveSections();
                var dSec = dSections.Count > 0 ? dSections[CurrentSectionIndex(dSections)] : null;
                if (dSec != null && dSec.Id == "primary")
                {
                    var sideways = direction < 0
                        ? Seams.GUIControl_controllerScrollSidewaysLeft
                        : Seams.GUIControl_controllerScrollSidewaysRight;
                    if (sideways != null)
                    {
                        Pump.NotePlayerNav();
                        try { sideways.Invoke(_gui, null); } catch { }
                        return;
                    }
                }
                Scaffold.SpeechService.Say(direction < 0 ? "No section left." : "No section right.", "Nav");
                return;
            }

            if (_sheetDef == null && _invDef == null)
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
            if (sec.InvDef != null
                && (sec.InvDef.Kind == InvKind.Grid || sec.InvDef.Kind == InvKind.Worn))
            {
                LandInvSection(sec, atLastRow, atFirstRow);
                return;
            }
            if (sec.PortraitRows)
            {
                LandPartySection(sec, atLastRow, atFirstRow);
                return;
            }
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

            if (sec.InvDef != null
                && (sec.InvDef.Kind == InvKind.Grid || sec.InvDef.Kind == InvKind.Worn))
            {
                InvFacetStep(sec, direction);
                return;
            }

            // Gate F: portrait rows have no lateral facets — Left/Right
            // re-speaks the row (the inspect block stays the reading-cursor
            // payload via the native X tooltip).
            if (sec.PortraitRows)
            {
                int slot = _invAnchor.TryGetValue(sec.Id, out int pa) ? pa : -1;
                string line = slot >= 0 && slot < sec.Count ? ComposePartyRow(sec, slot) : null;
                Scaffold.SpeechService.Say(line ?? "No columns.", "Nav");
                return;
            }

            // Gate E (ruling R2): the settings row's one lateral facet is the
            // setting's full description, queued through the shipped
            // name-dedup path (SliderArrowPatch.QueueDescription).
            if (_settingsScreen && sec.Id == "primary")
            {
                try
                {
                    object element = null;
                    int idx = CurrentIndex(sec.Canvas);
                    var elements = Seams.UICanvas_getScrollableElements?.Invoke(sec.Canvas, null) as IList;
                    if (elements != null && idx >= 0 && idx < elements.Count) element = elements[idx];
                    object sliderRow = element == null ? null
                        : Patches.SliderArrowPatch.RowForScrollableElement(sec.Canvas, element);
                    if (sliderRow != null) { Patches.SliderArrowPatch.QueueDescription(sliderRow); return; }
                }
                catch { }
                Scaffold.SpeechService.Say("No columns.", "Nav");
                return;
            }
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
                if (!Refresh()) return null;
                if (_invDef != null) return ComposeInvSelection(control, index);
                if (_sheetDef == null) return null;
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

        /// <summary>Gate-D: native selection writes on an owned inventory
        /// screen (the sheet's funnel index or a segment's own) compose the
        /// same R11 row the table speaks — the sheet's row index is a ROW at
        /// the surface's current column (UIInventorySheetBase.cs:586-604).
        /// Null for the button canvases (generic composition serves them).</summary>
        private static string ComposeInvSelection(object control, int index)
        {
            var sections = ResolveSections();
            object segment = null;
            if (Seams.InventorySegmentType != null
                && Seams.InventorySegmentType.IsInstanceOfType(control))
                segment = control;
            else if (Seams.UIInventorySheetBaseType != null
                && Seams.UIInventorySheetBaseType.IsInstanceOfType(control))
                segment = Pump.InventorySurfaceOf(control);
            if (segment == null) return null;

            foreach (var sec in sections)
            {
                if (sec.InvDef?.Kind != InvKind.Grid || !ReferenceEquals(sec.Canvas, segment)) continue;
                int w = GridWidth(segment);
                if (w <= 0) return null;
                int col = 0;
                try { col = (int)Seams.InvSegment_column.GetValue(segment); } catch { }
                int flat = OffsetOf(segment) + index * w + Math.Max(0, Math.Min(w - 1, col));
                if (flat < 0 || flat >= sec.Count) return "Empty.";
                return InvRowLine(sec, flat);
            }
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
            string cost, time, cascade, targets, aoe, prose;
            ParseComponentBlock(block, out cost, out time, out cascade, out targets, out aoe, out prose);

            if (sec.Id == "spells")
            {
                string schoolTier = SpellSchoolTier(backing);
                if (schoolTier != null) parts.Add(schoolTier);
                if (cost != null) parts.Add(cost);
                if (cascade != null) parts.Add(cascade);
                if (targets != null) parts.Add(targets);
                if (aoe != null) parts.Add(aoe);
            }
            else if (sec.Id == "maneuvers")
            {
                if (cost != null) parts.Add(cost);
                if (time != null) parts.Add(time);
                if (aoe != null) parts.Add(aoe);
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
        /// harvested, everything after them = prose. The game joins label
        /// and value with a TAB (TextTools.formateNameValuePair — never a
        /// colon; adversarial review gate C MUST-FIX: the colon match was
        /// dead code), so the split happens on the RAW line before cleaning
        /// collapses the tab to a space.</summary>
        private static void ParseComponentBlock(string block, out string cost, out string time,
            out string cascade, out string targets, out string aoe, out string prose)
        {
            cost = null; time = null; cascade = null; targets = null; aoe = null; prose = null;
            if (string.IsNullOrWhiteSpace(block)) return;
            var proseLines = new List<string>();
            bool inProse = false;
            foreach (var rawLine in block.Split('\n'))
            {
                string line = Patches.TextCleaner.CleanText(rawLine).Trim();
                if (line.Length == 0)
                {
                    if (cost != null || time != null || cascade != null || targets != null
                        || aoe != null || proseLines.Count > 0) inProse = true;
                    continue;
                }
                if (!inProse)
                {
                    if (line.StartsWith("[") && line.EndsWith("]")) continue; // the type line — section attributes it
                    int tab = rawLine.IndexOf('\t');
                    if (tab >= 0)
                    {
                        string label = Patches.TextCleaner.CleanText(rawLine.Substring(0, tab)).Trim().TrimEnd('.', ':');
                        string value = Patches.TextCleaner.CleanText(rawLine.Substring(tab + 1)).Trim();
                        if (value.Length > 0)
                        {
                            if (label.Equals("Cost", StringComparison.OrdinalIgnoreCase))
                            { cost = ExpandResource($"Cost {value}"); continue; }
                            if (label.Equals("Cascade", StringComparison.OrdinalIgnoreCase))
                            { cascade = EnsureDot($"Cascade {value}"); continue; }
                            if (label.Equals("Time Use", StringComparison.OrdinalIgnoreCase))
                            { time = EnsureDot($"Time use {value}"); continue; }
                            if (label.Equals("Targets", StringComparison.OrdinalIgnoreCase)
                                || label.Equals("Target", StringComparison.OrdinalIgnoreCase))
                            { targets = EnsureDot($"Targets {value}"); continue; }
                            if (label.Equals("AoE", StringComparison.OrdinalIgnoreCase))
                            { aoe = EnsureDot($"Area of effect {value}"); continue; }
                        }
                    }
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

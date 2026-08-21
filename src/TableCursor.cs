using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SkaldAccessibility
{
    /// <summary>
    /// The table-UI engine (docs\table-ui-design.md — the R15 build, gate A;
    /// owner build go-ahead 2026-08-21).
    ///
    /// On registered standard-UI screens the screen IS the table (no summon
    /// gesture — owner clarification 2026-08-21): ARROWS walk rows by driving
    /// the game's own funnel calls (setMouseToClosestOptionAbove/Below — native
    /// selection semantics, native paged-window slide, the shipped edge
    /// observer and hover-anchored selection join carry the row speech), and
    /// W/S walks SECTIONS (R15): landing announces label + census via the
    /// zone-label slot and restores the section's remembered row — the
    /// canvas's own currentSelectedButton, never a mod mirror (R2).
    ///
    /// Patch shape (the combat-latch precedent, receipt 7): W/A/S/D and the
    /// four arrows are swallowed at the SkaldIO binding choke while a table
    /// screen is active, AND every left-stick read is forced false at the
    /// already-detoured feed layer — native item-by-item stick nav cannot
    /// double-drive rows regardless of whether Unity's axes natively carry
    /// WASD (gate item 2 stays informational, not load-bearing, here too).
    ///
    /// Modality (the R10 lesson, inherited from the POI list and the K
    /// latch): the table suspends whole under popups, selector grids, and the
    /// game's own text entry — full native input returns; row and section
    /// positions live in the game's own canvases and survive the excursion.
    ///
    /// Sections resolve LIVE against the state's own GUIControl (per-frame
    /// memo, no cached canvases across frames beyond the buttons redirect,
    /// which is re-validated on read) — JournalState rebuilds its list content
    /// every frame and the settings family destroys element lists on scroll.
    /// A section that resolves to null or empty drops out of the walk (R4:
    /// no filler stops). All wordings provisional until owner calibration.
    ///
    /// Gate B registry: the clean single-list family — main menu, load/save,
    /// load module, journal/quests/factions. The combat log stays OUT (it
    /// extends CombatBaseState and lives inside the combat arrow claim);
    /// settings waits for its own gate (orphaned listButtons fence, rebind
    /// capture); the sheet/inventory families follow per §6.
    /// </summary>
    public static class TableCursor
    {
        // ---- config ----
        private static BepInEx.Configuration.ConfigEntry<bool> _enabled;

        public static void BindConfig(BepInEx.Configuration.ConfigFile config)
        {
            _enabled = config.Bind("Tables", "Engine", true,
                "Table navigation on standard UI screens (table-ui-design R15): arrows walk rows "
                + "through the game's own funnel calls, W/S walks sections. Off restores native-only nav.");
        }

        // ---- screen registry (gate B) ----
        // Value = the primary section's spoken label (wording provisional).
        // Every gate-B screen is a vertical two-stack at most: the state's own
        // native scrollable list, then the numeric option row when it is a
        // distinct populated canvas (SheetClass assigns the top-level
        // numericButtons field from its sheet complex — one seam covers all).
        private static readonly Dictionary<string, string> Screens
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

        private sealed class Section
        {
            public string Label;
            public object Canvas;
            public int Count;       // scrollable (content-bearing) elements
            public bool IsButtons;  // rides the getControllerScrollableList redirect while current
        }

        // ---- live screen memo (one reflection walk per frame) ----
        private static int _frame = -1;
        private static object _state;
        private static object _gui;
        private static string _primaryLabel;

        // The buttons redirect: while the Buttons section is current, the
        // game's own getControllerScrollableList answers with the numeric row
        // so the funnel calls, the edge observer, and the focus-routing truth
        // all follow the section. Cleared on state transition; inert whenever
        // the table is suspended (Active gates the read).
        private static object _redirectCanvas;

        // Re-entrancy guard: resolving the PRIMARY section asks the virtual
        // getControllerScrollableList, whose prefix asks us — the guard makes
        // the resolution see the native answer.
        private static bool _resolving;

        private static int _armedLogFrame = -1;

        /// <summary>True when the current state is a registered table screen
        /// (config on, gui resolvable). Memoized per frame; suspension gates
        /// (popup / grid / text entry) are layered on in Active().</summary>
        private static bool Refresh()
        {
            if (Time.frameCount == _frame) return _primaryLabel != null;
            _frame = Time.frameCount;
            _state = null; _gui = null; _primaryLabel = null;
            if (_enabled == null || !_enabled.Value) return false;
            try
            {
                object s = Pump.CurrentStateObject();
                if (s == null) return false;
                if (!Screens.TryGetValue(s.GetType().Name, out string label)) return false;
                object gui = Seams.StateBase_guiControl?.GetValue(s);
                if (gui == null) return false;
                _state = s; _gui = gui; _primaryLabel = label;
                return true;
            }
            catch { return false; }
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

        /// <summary>The table's whole modality: registered screen AND no
        /// excursion owns the input (the R10 suspension pattern — an unguarded
        /// claim leaves a popup or grid unnavigable).</summary>
        internal static bool Active()
        {
            if (!Refresh()) return false;
            if (Patches.ControllerFeedPatch.TextEntryActive()) return false;
            if (PopupUp() || Patches.GridNavigationPatch.GridActive()) return false;
            return true;
        }

        /// <summary>Force-false half of the claim (consulted first in every
        /// left-stick postfix, beside the combat latch's identical guard).</summary>
        internal static bool ClaimsStick => Active();

        /// <summary>Binding-route half of the claim, consulted at the SkaldIO
        /// choke: while the table is live the game goes blind to the four
        /// arrows and W/A/S/D. Escape, numbers, Z/X, Q/E stay native.</summary>
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

        /// <summary>The section redirect, consulted by the SheetClass
        /// getControllerScrollableList prefix. Null = no override (native
        /// answer stands). Re-validated on read: the canvas must still hold
        /// content-bearing elements.</summary>
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

        /// <summary>Called from InputHandler each frame (after the review
        /// layer and the grid driver). Consumed presses return true.</summary>
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

            if (up || down) { RowStep(up); return true; }
            if (left || right) { SpeakFacet(); return true; }
            if (w || s) { SectionStep(w ? -1 : +1); return true; }
            // A/D: every gate-B screen is a vertical stack — lateral refusal
            // receipt, never silent (R15). Wording at calibration.
            Scaffold.SpeechService.Say(a ? "No section left." : "No section right.", "Nav");
            return true;
        }

        /// <summary>Reset with the screen. Also the receipt-5 aid: log the
        /// registered screen's resolved section map once per entry.</summary>
        public static void OnStateTransition()
        {
            _redirectCanvas = null;
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

        // ---- rows ----

        /// <summary>One row step through the game's own funnel call. The
        /// native path does everything: selection move, paged-window slide at
        /// the edge (the B4 observer speaks it), hover-anchored selection join
        /// (the row line), panel writes. The explicit park mirrors
        /// GridNavigationPatch.Snap — setMouseToSelectedOption is gated on
        /// snapToOptions, which not every GUIControl sets.</summary>
        private static void RowStep(bool upward)
        {
            try
            {
                Pump.NotePlayerNav();
                var funnel = upward
                    ? Seams.GUIControl_setMouseToClosestOptionAbove
                    : Seams.GUIControl_setMouseToClosestOptionBelow;
                funnel?.Invoke(_gui, null);
                Park(CurrentCanvas());
            }
            catch (Exception ex) { Scaffold.Log.Throttled("table.row", ex.Message); }
        }

        /// <summary>Left/Right: the facet layer. Gate-B rows are single-facet
        /// — the one facet is the composed row, so a lateral press re-reads
        /// the current row (the parked-facet idiom degenerates to a row
        /// re-read on facetless screens). Never silent.</summary>
        private static void SpeakFacet()
        {
            string line = null;
            try { line = Pump.CurrentLineOf(CurrentCanvas()); } catch { }
            Scaffold.SpeechService.Say(line ?? "No columns.", "Nav");
        }

        // ---- sections ----

        private static void SectionStep(int direction)
        {
            List<Section> sections;
            try { sections = ResolveSections(); }
            catch (Exception ex) { Scaffold.Log.Throttled("table.section", ex.Message); return; }
            if (sections.Count == 0)
            {
                Scaffold.SpeechService.Say("No sections.", "Nav");
                return;
            }

            int current = CurrentSectionIndex(sections);
            int next = current + direction;
            if (next < 0) { Scaffold.SpeechService.Say("No section above.", "Nav"); return; }
            if (next >= sections.Count) { Scaffold.SpeechService.Say("No section below.", "Nav"); return; }
            LandSection(sections[next]);
        }

        /// <summary>Section landing (R15): label + census ride the zone-label
        /// slot onto the restored row's own line (one utterance, the
        /// NoteZoneLabel idiom); the remembered row is the canvas's OWN
        /// currentSelectedButton, clamped the way the game's bound-on-read
        /// clamp settles it; the write goes through the game's own setter so
        /// the selection join voices the landing.</summary>
        private static void LandSection(Section section)
        {
            try
            {
                _redirectCanvas = section.IsButtons ? section.Canvas : null;
                Pump.NotePlayerNav();

                int row = 0;
                try
                {
                    if (Seams.UICanvas_currentSelectedButton != null)
                        row = (int)Seams.UICanvas_currentSelectedButton.GetValue(section.Canvas);
                }
                catch { }
                if (row < 0) row = 0;
                if (row >= section.Count) row = section.Count - 1;

                Pump.NoteZoneLabel($"{section.Label}, {section.Count}");
                Seams.UICanvas_setCurrentSelectedButton?.Invoke(section.Canvas, new object[] { row });
                Park(section.Canvas);
                Scaffold.Log.Debug("Gate",
                    $"table section land: {section.Label} count={section.Count} row={row}");
            }
            catch (Exception ex) { Scaffold.Log.Throttled("table.section", ex.Message); }
        }

        private static int CurrentSectionIndex(List<Section> sections)
        {
            object redirect = _redirectCanvas;
            if (redirect != null)
            {
                for (int i = 0; i < sections.Count; i++)
                    if (ReferenceEquals(sections[i].Canvas, redirect)) return i;
                // The redirected canvas vanished from the walk (emptied /
                // rebuilt): drop the redirect, stand on the primary.
                _redirectCanvas = null;
            }
            return 0;
        }

        /// <summary>The section walk, resolved live: [native scrollable list,
        /// numeric row] with null/empty/duplicate entries dropped. On
        /// single-list screens (menu, journal — its numeric row is set empty)
        /// this collapses to one section and W/S becomes pure refusals.</summary>
        private static List<Section> ResolveSections()
        {
            var sections = new List<Section>(2);
            object primary = NativeScrollableList();
            int primaryCount = ScrollableCount(primary);
            if (primary != null && primaryCount > 0)
                sections.Add(new Section { Label = _primaryLabel, Canvas = primary, Count = primaryCount, IsButtons = false });

            object numeric = null;
            try { numeric = Seams.GUIControl_numericButtons?.GetValue(_gui); } catch { }
            if (numeric != null && !ReferenceEquals(numeric, primary))
            {
                int numericCount = ScrollableCount(numeric);
                if (numericCount > 0)
                    sections.Add(new Section { Label = ButtonsLabel, Canvas = numeric, Count = numericCount, IsButtons = true });
            }
            return sections;
        }

        /// <summary>The state's native scrollable list, bypassing our own
        /// redirect (the _resolving guard). Reflection dispatches virtually,
        /// so per-screen overrides and the game's own routing are honored.</summary>
        private static object NativeScrollableList()
        {
            if (Seams.GUIControl_getControllerScrollableList == null || _gui == null) return null;
            _resolving = true;
            try { return Seams.GUIControl_getControllerScrollableList.Invoke(_gui, null); }
            catch { return null; }
            finally { _resolving = false; }
        }

        /// <summary>The canvas the game would serve RIGHT NOW (redirect
        /// included) — the park target and facet source.</summary>
        private static object CurrentCanvas()
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

        /// <summary>Explicit virtual-mouse park on the canvas — drills to its
        /// current selected element (the native snap's own idiom,
        /// GUIControl.cs:1845); mirrors GridNavigationPatch.Snap's offsets.</summary>
        private static void Park(object canvas)
        {
            if (canvas == null) return;
            try { Seams.GUIControl_setMouseToUIElement?.Invoke(_gui, new object[] { canvas, 8, -8 }); }
            catch { }
        }
    }
}

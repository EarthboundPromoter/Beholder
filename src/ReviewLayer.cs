using System;
using System.Collections.Generic;
using UnityEngine;

namespace SkaldAccessibility
{
    /// <summary>
    /// WP10: the review layer — a navigable buffer over the focused element's
    /// panel text (owner-approved spec, build-plan Â§WP10).
    ///
    /// The buffer is virtual: two cursor ints over the game's latest rendered
    /// panel text (captured raw, with markup, from the panel-class content
    /// sources). Composition is lazy at keypress time — nothing cached, no
    /// staleness. Sections come from the game's own markup grammar:
    /// HEADER_TAG lines open sections; contiguous ATTRIBUTE_NAME/VALUE_TAG pair
    /// runs form stats sections whose elements are the pairs (comparison colors
    /// transcode to ", better"/", worse" per pair); blank lines split prose
    /// sections whose elements are sentences.
    ///
    /// Two doorways, one engine:
    ///   stateless cluster (always live, combat included):
    ///     Home/End = section walk, PgUp/PgDn = element within section
    ///   R toggle (for cluster-less keyboards):
    ///     Left/Right = section walk, Up/Down = element within section
    ///
    /// Input classes inside the toggle: navigation (WASD, Q/E) exits silently
    /// then acts; activation (Z, X, Enter, Space, numbers, U/I) is EATEN —
    /// review closes, "Closed." + terse re-anchor, nothing fires (the virtual
    /// mouse never moved, so a confirm from inside the buffer would be a blind
    /// click); dismissal (R, Esc, Backspace) exits with the press swallowed.
    /// The physical mouse is exempt from the eat. Swallowing rides the SkaldIO
    /// choke point (SkaldIOPatches) and the ControllerFeed emulation gate.
    /// </summary>
    public static class ReviewLayer
    {
        // ---- Panel capture (latest rendered panel-class text, raw markup) ----
        // TP1: provenance rides the capture — the source tag selects the
        // section map (Composer.SectionPanel) instead of being discarded at
        // the buffer door. Cursor policy (Sonnet find 2): a SOURCE change is a
        // genuinely different document — re-anchor; a same-source update is
        // the same logical document evolving (a ticking popup, a sheet body
        // under hover) — hold position, let the step clamps absorb shrink.
        private static string _panelRaw;
        private static string _panelSource;

        public static void NotePanel(string source, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            if (!string.Equals(source, _panelSource, StringComparison.Ordinal))
            {
                _section = 0;
                _element = -1;
            }
            _panelRaw = raw;
            _panelSource = source;
        }

        public static void ClearPanel() { _panelRaw = null; _panelSource = null; }

        // ---- Staged document (combat Layer 1, table-ui-design §6.18): a
        // pre-composed, pre-sectioned document staged by a cursor landing —
        // the combatant drilldown. Unlike the lazy panel capture, this is a
        // deliberate snapshot at landing time (capture-at-write); it WINS over
        // the panel capture until the next landing replaces or clears it, or
        // a state transition drops everything. The reading plane's grammar
        // (Home/End sections, PgUp/PgDn elements) is unchanged. ----
        private static List<Composer.PanelSection> _staged;
        private static string _stagedSource;

        internal static void NoteStagedDocument(string source, List<Composer.PanelSection> sections)
        {
            if (sections == null || sections.Count == 0) { ClearStaged(); return; }
            // Every stage is a fresh snapshot of a (possibly different)
            // object — a genuinely new document, so always re-anchor.
            _section = 0;
            _element = -1;
            _staged = sections;
            _stagedSource = source;
        }

        public static void ClearStaged() { _staged = null; _stagedSource = null; }

        private static bool PopupBlocking()
        {
            try
            {
                return Seams.PopUpControl_getCurrentPopUp != null
                    && Seams.PopUpControl_getCurrentPopUp.Invoke(null, null) != null;
            }
            catch { return false; }
        }

        // ---- Toggle state ----
        private static bool _active;
        private static int _eatUntilFrame = -1; // swallow tail after an eat-close
        private static int _section;
        private static int _element = -1; // -1 = section-level position

        public static bool Active => _active;

        /// <summary>True while activation-class emulations (triggers, X/Y/B
        /// buttons) must not fire — consulted by ControllerFeedPatch.</summary>
        public static bool EatingActivations()
            => _active || Time.frameCount <= _eatUntilFrame;

        /// <summary>Choke-point predicate (SkaldIOPatches): keys the game must
        /// not see while review is open (or during the eat tail of the closing
        /// press). Navigation-class keys are never swallowed — they exit-then-act.</summary>
        public static bool ShouldSwallowKey(KeyCode key)
        {
            if (!_active && Time.frameCount > _eatUntilFrame) return false;
            switch (key)
            {
                case KeyCode.LeftArrow:
                case KeyCode.RightArrow:
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                case KeyCode.Return:
                case KeyCode.Space:
                case KeyCode.Alpha1: case KeyCode.Alpha2: case KeyCode.Alpha3:
                case KeyCode.Alpha4: case KeyCode.Alpha5: case KeyCode.Alpha6:
                case KeyCode.Alpha7: case KeyCode.Alpha8: case KeyCode.Alpha9:
                case KeyCode.Escape:
                case KeyCode.Backspace:
                case KeyCode.R:
                case KeyCode.U:
                case KeyCode.I:
                case KeyCode.Z:
                case KeyCode.X:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Called from InputHandler each frame (after the text-entry
        /// gate). Returns true when the press was review's.</summary>
        public static bool ProcessInput()
        {
            // The toggle.
            if (Input.GetKeyDown(KeyCode.R))
            {
                if (_active) Close(announce: true, eat: true);
                else Open();
                return true;
            }

            // The stateless cluster — always live, no mode involved.
            if (Input.GetKeyDown(KeyCode.Home)) { StepSection(-1); return true; }
            if (Input.GetKeyDown(KeyCode.End)) { StepSection(+1); return true; }
            if (Input.GetKeyDown(KeyCode.PageUp)) { StepElement(-1); return true; }
            if (Input.GetKeyDown(KeyCode.PageDown)) { StepElement(+1); return true; }

            if (!_active) return false;

            // In-state arrows: horizontal walks structure, vertical drills.
            if (Input.GetKeyDown(KeyCode.LeftArrow)) { StepSection(-1); return true; }
            if (Input.GetKeyDown(KeyCode.RightArrow)) { StepSection(+1); return true; }
            if (Input.GetKeyDown(KeyCode.UpArrow)) { StepElement(-1); return true; }
            if (Input.GetKeyDown(KeyCode.DownArrow)) { StepElement(+1); return true; }

            // Dismissal class: swallowed exit.
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace))
            {
                Close(announce: true, eat: true);
                return true;
            }

            // Activation class: eaten — nothing fires, review closes, re-anchor.
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space)
                || Input.GetKeyDown(KeyCode.Z) || Input.GetKeyDown(KeyCode.X)
                || Input.GetKeyDown(KeyCode.U) || Input.GetKeyDown(KeyCode.I)
                || NumberPressed())
            {
                Close(announce: true, eat: true);
                return true;
            }

            // Navigation class: exit silently, let the game act on the press.
            if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.A)
                || Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.D)
                || Input.GetKeyDown(KeyCode.Q) || Input.GetKeyDown(KeyCode.E))
            {
                CloseSilent();
                return false;
            }

            return false;
        }

        private static bool NumberPressed()
        {
            for (KeyCode k = KeyCode.Alpha1; k <= KeyCode.Alpha9; k++)
                if (Input.GetKeyDown(k)) return true;
            return false;
        }

        /// <summary>Housekeeping at the drain: the review flag never survives
        /// the game's own text entry becoming active.</summary>
        public static void MaintainFromDrain()
        {
            if (_active && Patches.ControllerFeedPatch.TextEntryActive())
                CloseSilent();
        }

        /// <summary>Force-close from the state clock — a state transition
        /// unconditionally clears review and the captured panel.</summary>
        public static void OnStateTransition()
        {
            CloseSilent();
            ClearPanel();
            ClearStaged();
        }

        /// <summary>Silent close — used when navigation/focus movement is the
        /// exit; the resulting navigation speech is the anchor.</summary>
        public static void CloseSilent()
        {
            _active = false;
        }

        private static void Open()
        {
            if (Patches.ControllerFeedPatch.TextEntryActive()) return;
            var sections = Parse();
            if (sections.Count == 0)
            {
                Scaffold.SpeechService.Say("No details.", "Review");
                return;
            }
            _active = true;
            _section = 0;
            _element = -1;
            SpeakSection(sections);
        }

        private static void Close(bool announce, bool eat)
        {
            _active = false;
            if (eat) _eatUntilFrame = Time.frameCount + 2; // covers FixedUpdate straddle
            if (announce)
            {
                Scaffold.SpeechService.Say("Closed.", "Review");
                string anchor = Pump.CurrentFocusLine();
                if (anchor != null) Scaffold.SpeechService.SayQueued(anchor, "Review");
            }
        }

        /// <summary>Focus moved — cursors reset so the next review starts at the
        /// top of whatever the game has rendered for the new focus.</summary>
        public static void OnFocusChanged()
        {
            _section = 0;
            _element = -1;
            if (_active) CloseSilent();
        }

        // ---- Stepping (both doorways share cursors and composition) ----

        private static void StepSection(int direction)
        {
            var sections = Parse();
            if (sections.Count == 0) { Scaffold.SpeechService.Say("No details.", "Review"); return; }
            int next = _section + direction;
            if (next < 0) { Scaffold.SpeechService.Say("First section.", "Review"); _section = 0; return; }
            if (next >= sections.Count) { Scaffold.SpeechService.Say("Last section.", "Review"); _section = sections.Count - 1; return; }
            _section = next;
            _element = -1;
            SpeakSection(sections);
        }

        private static void StepElement(int direction)
        {
            var sections = Parse();
            if (sections.Count == 0) { Scaffold.SpeechService.Say("No details.", "Review"); return; }
            if (_section >= sections.Count) _section = sections.Count - 1;
            var elements = sections[_section].Elements;
            if (elements.Count == 0) { Scaffold.SpeechService.Say("Empty section.", "Review"); return; }
            int next = (_element < 0 && direction > 0) ? 0 : _element + direction;
            if (next < 0) { Scaffold.SpeechService.Say("Start of section.", "Review"); _element = 0; return; }
            if (next >= elements.Count) { Scaffold.SpeechService.Say("End of section.", "Review"); _element = elements.Count - 1; return; }
            _element = next;
            Scaffold.SpeechService.Say($"{elements[_element]}, {_element + 1} of {elements.Count}.", "Review");
        }

        private static void SpeakSection(List<Composer.PanelSection> sections)
        {
            if (_section >= sections.Count) _section = sections.Count - 1;
            var s = sections[_section];
            Scaffold.SpeechService.Say($"{s.FullText}, section {_section + 1} of {sections.Count}.", "Review");
        }

        // ---- Sectioning (TP1): migrated into the Composer — provenance picks
        // the map, markup structures within; this layer keeps only cursors and
        // keys. The standing status section trails every document: the strip's
        // facts, composed LIVE at keypress from the game's own composer (never
        // cached, never stale), at a fixed last-section address. ----

        private static List<Composer.PanelSection> Parse()
        {
            // A staged document (combatant drilldown) wins over the lazy panel
            // capture while present — EXCEPT under a popup, whose block must
            // stay reachable on the reading plane (the popup is the world in
            // front of the player). No trailing Status section: a staged
            // document is a snapshot of one object, not the world.
            if (_staged != null && _staged.Count > 0 && !PopupBlocking())
                return new List<Composer.PanelSection>(_staged);
            PanelPolicy.EnsureTags();
            var sections = Composer.SectionPanel(_panelSource, _panelRaw);
            AppendStatusSection(sections);
            return sections;
        }

        private static void AppendStatusSection(List<Composer.PanelSection> sections)
        {
            try
            {
                var f = PanelPolicy.LiveFacts();
                if (f == null || !f.Valid) return;
                var s = new Composer.PanelSection { Title = "Status" };
                // Element order is SHAPE-STABLE under weather toggling (Sonnet
                // find 3): weather is the only fact that comes and goes, so it
                // sits LAST — mid-browse indices for the fixed facts never
                // shift when a shower starts between two keypresses.
                if (f.Time != null) s.Elements.Add(f.Time);
                if (f.Day != null) s.Elements.Add(f.Day);
                if (f.X != null) s.Elements.Add(f.X);
                if (f.Y != null) s.Elements.Add(f.Y);
                if (f.Phase != null) s.Elements.Add(f.Phase);
                if (f.Weather != null) s.Elements.Add(f.Weather);
                if (s.Elements.Count == 0) return;
                s.FullText = "Status, " + string.Join(", ", s.Elements.ToArray());
                sections.Add(s);
            }
            catch { }
        }
    }
}

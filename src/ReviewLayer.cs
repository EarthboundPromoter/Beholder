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
        private static readonly EatTail _tail = new EatTail(); // swallow tail after an eat-close: one game tick
        private static int _section;
        private static int _element = -1; // -1 = section-level position

        public static bool Active => _active;

        /// <summary>True while activation-class emulations (triggers, X/Y/B
        /// buttons) must not fire — consulted by ControllerFeedPatch.</summary>
        public static bool EatingActivations()
            => _active || _tail.Holds;

        /// <summary>Choke-point predicate (SkaldIOPatches): keys the game must
        /// not see while review is open (or during the eat tail of the closing
        /// press). Navigation-class keys are never swallowed — they exit-then-act.</summary>
        public static bool ShouldSwallowKey(KeyCode key)
        {
            if (!_active && !_tail.Holds) return false;
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
            if (eat) _tail.Arm(2); // one game tick (EatTail); the old two-frame window is the fallback
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

        /// <summary>Home/End: the section walk WRAPS (nav revision §4 — the
        /// dead ends go; the seam is named). With the Rules tail, one Home
        /// press from the top lands on the definitions.</summary>
        private static void StepSection(int direction)
        {
            var sections = Parse();
            if (sections.Count == 0) { Scaffold.SpeechService.Say("No details.", "Review"); return; }
            int next = _section + direction;
            bool wrapped = next < 0 || next >= sections.Count;
            _section = ((next % sections.Count) + sections.Count) % sections.Count;
            _element = -1;
            SpeakSection(sections, wrapped ? "Wrapped. " : null);
        }

        /// <summary>PgUp/PgDn: elements FLOW across section seams, announced
        /// composed ("Stats. Soak 0 to 9."); past the document's ends the
        /// flow wraps with the seam named (nav revision §4).</summary>
        private static void StepElement(int direction)
        {
            var sections = Parse();
            if (sections.Count == 0) { Scaffold.SpeechService.Say("No details.", "Review"); return; }
            if (_section >= sections.Count) _section = sections.Count - 1;
            string prefix = null;
            // Sentinel landing (Sonnet MUST-FIX 2026-08-23): from the fresh
            // -1 cursor, PgDn enters at the section's FIRST element and PgUp
            // at its LAST — the old asymmetric sentinel made a first PgUp
            // skip the whole current section (and wrap the document).
            int next = _element >= 0 ? _element + direction
                : direction > 0 ? 0 : sections[_section].Elements.Count - 1;

            int guard = sections.Count + 1;   // skip empty sections, at most one lap
            while (guard-- > 0)
            {
                var elements = sections[_section].Elements;
                if (next >= 0 && next < elements.Count) break;
                // Crossing a seam: move to the neighbour section, land on its
                // near edge, and let its label ride the element.
                int dir = next < 0 ? -1 : +1;
                int nextSec = _section + dir;
                if (nextSec < 0 || nextSec >= sections.Count)
                {
                    prefix = "Wrapped. ";
                    nextSec = ((nextSec % sections.Count) + sections.Count) % sections.Count;
                }
                _section = nextSec;
                var landed = sections[_section];
                if (prefix == null)
                    prefix = (landed.Title ?? $"Section {_section + 1}") + ". ";
                next = dir < 0 ? landed.Elements.Count - 1 : 0;
                // An empty landed section leaves next out of range in the
                // SAME direction — the loop flows onward.
            }
            var els = sections[_section].Elements;
            if (els.Count == 0) { Scaffold.SpeechService.Say("Empty section.", "Review"); return; }
            _element = Math.Max(0, Math.Min(next, els.Count - 1));
            Scaffold.SpeechService.Say(
                $"{prefix}{els[_element]}, {_element + 1} of {els.Count}.", "Review");
        }

        private static void SpeakSection(List<Composer.PanelSection> sections, string prefix = null)
        {
            if (_section >= sections.Count) _section = sections.Count - 1;
            var s = sections[_section];
            Scaffold.SpeechService.Say($"{prefix}{s.FullText}, section {_section + 1} of {sections.Count}.", "Review");
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
            // Examine surfaces compose, never parse (nav revision §4): a
            // captured item tip becomes the R11 decision-weight sections;
            // anything else keeps the native-structure parse (the overland
            // panel is the case that already works).
            List<Composer.PanelSection> sections = null;
            if (_panelSource == "Tooltip")
                sections = TableCursor.ComposeExamineDocument(_panelRaw);
            bool composed = sections != null;
            if (sections == null)
                sections = Composer.SectionPanel(_panelSource, _panelRaw);
            // The Rules tail rides BOTH examine sources (fix 2026-08-23: it
            // was fenced to the X-tip capture, but on table screens the
            // reader mostly serves the game's select-updated description
            // panel — the shipped route for sheet/item descriptions — so the
            // mechanics definitions never appeared).
            if (_panelSource == "Tooltip" || _panelSource == "SheetDesc")
                AppendRulesTail(sections, _panelRaw);
            if (!composed) AppendStatusSection(sections);
            return sections;
        }

        /// <summary>The Rules tail (nav revision §4): keywords from the
        /// game's own Rules catalog (conditions, abilities, attributes,
        /// classes — the green-highlighted tooltip words) found in the ROOT
        /// tip text append ONE trailing section; elements = "Name: full
        /// description.", deduped, first-mention order. LEVEL-1 CAP by
        /// construction — keywords inside a keyword's description are heard
        /// as names, never expanded (the anti-recursion ruling). No mouse,
        /// no render dependency: resolution is a catalog lookup.</summary>
        private static void AppendRulesTail(List<Composer.PanelSection> sections, string raw)
        {
            try
            {
                if (sections == null || string.IsNullOrEmpty(raw)) return;
                object catalog = Seams.ToolTipControl_getRulesToolTips?.Invoke(null, null);
                var keywords = catalog == null ? null
                    : Seams.ToolTipCategory_getKeywords?.Invoke(catalog, null) as System.Collections.IList;
                if (keywords == null || keywords.Count == 0) return;

                var hits = new List<KeyValuePair<int, string>>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (object k in keywords)
                {
                    string kw = k as string;
                    if (string.IsNullOrEmpty(kw) || kw.Length < 3 || seen.Contains(kw)) continue;
                    int at = BoundedIndexOf(raw, kw);
                    if (at < 0) continue;
                    seen.Add(kw);
                    hits.Add(new KeyValuePair<int, string>(at, kw));
                }
                if (hits.Count == 0) return;
                hits.Sort((x, y) => x.Key.CompareTo(y.Key));

                var rules = new Composer.PanelSection { Title = "Rules" };
                // Concept-level dedup (Sonnet MUST-FIX): the catalog registers
                // casing variants per concept that resolve to the same tip —
                // dedup on the RESOLVED name, not the literal keyword.
                var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var hit in hits)
                {
                    if (rules.Elements.Count >= 12)
                    {
                        Scaffold.Log.Debug("Review", $"rules tail capped at 12 of {hits.Count}");
                        break;
                    }
                    object tip = Seams.ToolTipCategory_getToolTip?.Invoke(catalog, new object[] { hit.Value });
                    if (tip == null) continue;
                    string name = Patches.TextCleaner.CleanText(
                        Seams.SkaldBaseObject_getName?.Invoke(tip, null) as string ?? hit.Value).Trim();
                    if (name.Length == 0 || !resolved.Add(name)) continue;
                    string desc = Patches.TextCleaner.CleanText(
                        (Seams.SkaldBaseObject_getFullDescription?.Invoke(tip, null) as string ?? "")
                        .Replace("\n", " ")).Trim();
                    // The catalog ToolTip's own getFullDescription prepends the
                    // uppercased name header (ToolTipControl.cs:152-155) — the
                    // cleaner strips the tags, not the text (Sonnet MUST-FIX:
                    // every entry spoke its name twice). Strip the echo.
                    if (desc.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                        desc = desc.Substring(name.Length).TrimStart(':', ' ', '-', '.');
                    if (string.IsNullOrWhiteSpace(desc)) continue;
                    rules.Elements.Add($"{name}: {desc}");
                }
                if (rules.Elements.Count == 0) return;
                rules.FullText = $"Rules, {rules.Elements.Count}";
                sections.Add(rules);
                Scaffold.Log.Debug("Review",
                    $"rules tail: {rules.Elements.Count} entries from {hits.Count} keyword hits ({_panelSource})");
            }
            catch (Exception ex) { Scaffold.Log.Throttled("review.rules", ex.Message); }
        }

        /// <summary>Word-bounded keyword search, mirroring the game's own
        /// highlighter contract (UITextBlock.identifyTooltipKeywords: letters
        /// on either side of the match refuse it — Sonnet MUST-FIX: "Rage"
        /// must not fire inside "outrageous").</summary>
        private static int BoundedIndexOf(string raw, string kw)
        {
            int from = 0;
            while (from < raw.Length)
            {
                int at = raw.IndexOf(kw, from, StringComparison.Ordinal);
                if (at < 0) return -1;
                bool leftOk = at == 0 || !char.IsLetter(raw[at - 1]);
                int end = at + kw.Length;
                bool rightOk = end >= raw.Length || !char.IsLetter(raw[end]);
                if (leftOk && rightOk) return at;
                from = at + 1;
            }
            return -1;
        }

        /// <summary>A table landing moved the anchor: a captured tooltip is
        /// about the PREVIOUS focus — drop it so the cluster never narrates
        /// stale item data as if it were the new row (Sonnet MUST-FIX
        /// 2026-08-23; a changed capture is a new document, a moved anchor is
        /// no document).</summary>
        internal static void InvalidateTooltipCapture()
        {
            if (_panelSource != "Tooltip") return;
            ClearPanel();
            // The forwarded-panel dedup record must die with the capture
            // (fix 2026-08-23): a second X on the SAME item re-renders the
            // identical tip text, and a surviving record swallowed the
            // re-capture — the reader then served the previous document.
            Pump.ForgetForwardedPanel("Tooltip");
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

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SkaldAccessibility
{
    /// <summary>
    /// WP10: the review layer — a navigable buffer over the focused element's
    /// panel text (owner-approved spec, build-plan §WP10).
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
        private static string _panelRaw;

        public static void NotePanel(string raw)
        {
            if (!string.IsNullOrWhiteSpace(raw)) _panelRaw = raw;
        }

        public static void ClearPanel() => _panelRaw = null;

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

        private static void SpeakSection(List<Section> sections)
        {
            if (_section >= sections.Count) _section = sections.Count - 1;
            var s = sections[_section];
            Scaffold.SpeechService.Say($"{s.FullText}, section {_section + 1} of {sections.Count}.", "Review");
        }

        // ---- Sectioning engine (the game's markup grammar is the schema) ----

        private class Section
        {
            public string FullText;
            public List<string> Elements = new List<string>();
        }

        private enum Kind { None, Stats, Prose }

        private static string _headerTag, _attrNameTag, _attrValueTag, _greenTag, _redTag;
        private static bool _tagsInitialized;

        /// <summary>Tag VALUES read lazily (post-ready — review only runs on
        /// rendered panels) through the WP8 Seams metadata handles.</summary>
        private static void InitTags()
        {
            _tagsInitialized = true;
            _headerTag = Seams.TagValue(Seams.C64_HeaderTag);
            _attrNameTag = Seams.TagValue(Seams.C64_AttributeNameTag);
            _attrValueTag = Seams.TagValue(Seams.C64_AttributeValueTag);
            _greenTag = Seams.TagValue(Seams.C64_GreenLightTag);
            _redTag = Seams.TagValue(Seams.C64_RedLightTag);
            if (_headerTag == null || _attrNameTag == null)
                Plugin.Logger?.LogWarning("[Review] Markup tags incomplete — sectioning degrades to paragraphs "
                    + $"(header={_headerTag != null} attrName={_attrNameTag != null})");
        }

        private static List<Section> Parse()
        {
            var sections = new List<Section>();
            string raw = _panelRaw;
            if (string.IsNullOrWhiteSpace(raw)) return sections;
            if (!_tagsInitialized) InitTags();

            Kind kind = Kind.None;
            var statElements = new List<string>();
            var proseText = "";

            void CloseCurrent()
            {
                if (kind == Kind.Stats && statElements.Count > 0)
                {
                    sections.Add(new Section
                    {
                        FullText = string.Join(", ", statElements.ToArray()),
                        Elements = new List<string>(statElements),
                    });
                }
                else if (kind == Kind.Prose && !string.IsNullOrWhiteSpace(proseText))
                {
                    var s = new Section { FullText = proseText.Trim() };
                    s.Elements.AddRange(SplitSentences(s.FullText));
                    sections.Add(s);
                }
                kind = Kind.None;
                statElements.Clear();
                proseText = "";
            }

            foreach (string line in raw.Split('\n'))
            {
                string cleaned = Patches.TextCleaner.CleanText(line);
                bool blank = string.IsNullOrWhiteSpace(cleaned);
                if (blank) { CloseCurrent(); continue; }

                bool header = _headerTag != null && line.Contains(_headerTag);
                bool pair = !header && _attrNameTag != null
                    && (line.Contains(_attrNameTag) || (_attrValueTag != null && line.Contains(_attrValueTag)));

                if (header)
                {
                    CloseCurrent();
                    var s = new Section { FullText = cleaned.Trim() };
                    s.Elements.Add(cleaned.Trim());
                    sections.Add(s);
                }
                else if (pair)
                {
                    if (kind != Kind.Stats) CloseCurrent();
                    kind = Kind.Stats;
                    statElements.Add(TranscodeComparison(line, cleaned.Trim()));
                }
                else
                {
                    if (kind != Kind.Prose) CloseCurrent();
                    kind = Kind.Prose;
                    proseText = proseText.Length == 0 ? cleaned.Trim() : proseText + " " + cleaned.Trim();
                }
            }
            CloseCurrent();
            return sections;
        }

        /// <summary>Per-pair comparison transcode: the game's green/red value
        /// coloring is its only carrier of better/worse-than-equipped — turn it
        /// into words. Per stat only; aggregate verdicts stay forbidden.</summary>
        private static string TranscodeComparison(string rawLine, string cleaned)
        {
            if (_greenTag != null && rawLine.Contains(_greenTag)) return cleaned + ", better";
            if (_redTag != null && rawLine.Contains(_redTag)) return cleaned + ", worse";
            return cleaned;
        }

        private static IEnumerable<string> SplitSentences(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool ender = c == '.' || c == '!' || c == '?';
                if (ender && (i + 1 >= text.Length || text[i + 1] == ' '))
                {
                    string sentence = text.Substring(start, i - start + 1).Trim();
                    if (sentence.Length > 0) yield return sentence;
                    start = i + 1;
                }
            }
            if (start < text.Length)
            {
                string tail = text.Substring(start).Trim();
                if (tail.Length > 0) yield return tail;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SkaldAccessibility
{
    /// <summary>
    /// TP2: the dialogue text cursor — W/S extended seamlessly from the scene
    /// option funnel up into the prose, A/D hopping the pane's own topic
    /// words (owner rulings 2026-08-18).
    ///
    /// The idiom: in any scene-family state (SceneBase — dialogue, interact,
    /// attack prompt, random encounter, game over; uniform per ruling 4), W
    /// from the TOP option crosses into the text at its LAST sentence and
    /// walks upward (bottom-up — the most recent unit first); S walks back
    /// down and, past the last sentence, returns to the first option. A/D hop
    /// topic-to-topic from anywhere (position-global, the scan-key idiom):
    /// the hop drops the virtual mouse on the word element itself, so Z is
    /// the NATIVE left-click and the lore tooltip raises through the game's
    /// own machinery (UITextBlock.getTooltipText — the game enumerates the
    /// topics for us; toolTipWords is the rendered truth).
    ///
    /// Sentence source: the rendered block's own `content` field — NEVER the
    /// scene composer (SceneNode.getDescription re-runs processString on
    /// every call and would re-fire embedded script writers — audit flag 6).
    /// Content is read fresh at every keypress (the lazy idiom, nothing
    /// cached); a node change (same state, new prose) exits text mode.
    ///
    /// Input path: the claims are called from ControllerFeedPatch's stick
    /// postfixes — the already-detoured layer (the a8de251/Mono-inline
    /// lesson: never detour the one-line SkaldIO wrappers). A claimed press
    /// returns the read to false, so the funnel never sees it. Entering the
    /// text parks the mouse on the pane itself — clicks on scene prose have
    /// NO consumer (verified: SceneState/SceneBaseState read no mouse), so a
    /// stray Z in text mode is inert instead of a blind option click.
    ///
    /// Player actions interrupt mod speech (ruling 5): every cursor utterance
    /// speaks with interrupt priority.
    /// </summary>
    internal static class DialogueCursor
    {
        private static bool _textMode;
        private static int _sentence = -1;
        private static int _topic = -1;
        private static string _lastProse; // raw content the indices refer to

        // The Word type is a private nested class — its highlightWord field is
        // resolved from the first live element and cached per type.
        private static Type _wordType;
        private static FieldInfo _wordHighlight;

        internal static void OnStateTransition()
        {
            _textMode = false;
            _sentence = -1;
            _topic = -1;
            _lastProse = null;
        }

        // ---- Gate ----

        private static bool SceneReady(out object state, out object gui)
        {
            state = null; gui = null;
            try
            {
                if (Patches.ControllerFeedPatch.TextEntryActive()) return false;
                if (ReviewLayer.Active) return false;
                if (Seams.PopUpControl_getCurrentPopUp != null
                    && Seams.PopUpControl_getCurrentPopUp.Invoke(null, null) != null) return false;
                state = Pump.CurrentStateObject();
                if (state == null || Seams.SceneBaseStateType == null
                    || !Seams.SceneBaseStateType.IsInstanceOfType(state)) return false;
                gui = Seams.StateBase_guiControl?.GetValue(state);
                return gui != null;
            }
            catch { return false; }
        }

        // ---- Claims (ControllerFeedPatch stick postfixes) ----

        /// <summary>W / stick up. True = claimed (the funnel must not see it).</summary>
        internal static bool ClaimStickUp()
        {
            if (!SceneReady(out object state, out object gui))
            {
                if (_textMode) _textMode = false; // gate broke mid-mode: silent release
                return false;
            }
            SyncProse(gui);
            if (_textMode)
            {
                Pump.NotePlayerNav();
                WalkText(gui, -1);
                return true;
            }
            if (!FocusAtTopOption(gui)) return false;
            var sentences = Sentences(gui);
            if (sentences.Count == 0) return false; // nothing to enter — funnel clamps natively
            Pump.NotePlayerNav();
            EnterTextMode(gui, sentences);
            return true;
        }

        /// <summary>S / stick down.</summary>
        internal static bool ClaimStickDown()
        {
            if (!SceneReady(out object state, out object gui))
            {
                if (_textMode) _textMode = false;
                return false;
            }
            if (!_textMode) return false;
            SyncProse(gui);
            if (!_textMode) return false; // node changed under us — exited
            Pump.NotePlayerNav();
            WalkText(gui, +1);
            return true;
        }

        /// <summary>A/D / stick sideways: topic hop, position-global, any time
        /// in a scene state (natively dead keys there — sideways scroll routes
        /// to the null sheetComplex).</summary>
        internal static bool ClaimStickSideways(int direction)
        {
            if (!SceneReady(out object state, out object gui)) return false;
            SyncProse(gui);
            Pump.NotePlayerNav();
            HopTopic(gui, direction);
            return true;
        }

        // ---- Text walk ----

        private static void EnterTextMode(object gui, List<string> sentences)
        {
            _textMode = true;
            _sentence = sentences.Count - 1;
            ClearTooltip();
            ParkMouseOnPane(gui);
            SpeakSentence(gui, sentences);
        }

        private static void WalkText(object gui, int direction)
        {
            var sentences = Sentences(gui);
            if (sentences.Count == 0) { ExitToOptions(gui); return; }
            if (_sentence >= sentences.Count) _sentence = sentences.Count - 1;
            int next = _sentence + direction;
            if (next < 0)
            {
                Scaffold.SpeechService.Say("Top.", "Dialogue");
                _sentence = 0;
                return;
            }
            if (next >= sentences.Count)
            {
                ExitToOptions(gui);
                return;
            }
            _sentence = next;
            ClearTooltip();
            SpeakSentence(gui, sentences);
        }

        private static void SpeakSentence(object gui, List<string> sentences)
        {
            string sentence = sentences[_sentence];
            int topics = CountTopicsIn(gui, sentence);
            string trailer = topics == 1 ? ", 1 topic" : topics > 1 ? $", {topics} topics" : "";
            Scaffold.SpeechService.Say(
                $"{sentence}{trailer}, {_sentence + 1} of {sentences.Count}.", "Dialogue");
        }

        private static void ExitToOptions(object gui)
        {
            _textMode = false;
            ClearTooltip();
            try { Seams.GUIControl_setMouseToSelectedOption?.Invoke(gui, null); }
            catch { }
            // The snap lands on the funnel's current option (index unchanged
            // = the first option); the index didn't change, so the selection
            // join stays silent — anchor with the focus line ourselves.
            string anchor = Pump.CurrentFocusLine();
            Scaffold.SpeechService.Say(anchor ?? "Options.", "Dialogue");
        }

        // ---- Topics ----

        private sealed class TopicStop
        {
            public object Word;      // the first Word element of the run
            public string Keyword;   // highlightWord — the game's match key
        }

        private static void HopTopic(object gui, int direction)
        {
            var stops = BuildStops(gui);
            if (stops.Count == 0)
            {
                Scaffold.SpeechService.Say("No topics.", "Dialogue");
                return;
            }
            if (_topic < 0) _topic = direction > 0 ? 0 : stops.Count - 1;
            else
            {
                _topic += direction;
                if (_topic >= stops.Count) _topic = 0;       // wrap — the scan idiom
                else if (_topic < 0) _topic = stops.Count - 1;
            }
            var stop = stops[_topic];
            ClearTooltip();
            try { Seams.GUIControl_setMouseToUIElement?.Invoke(gui, new object[] { stop.Word, 1, -1 }); }
            catch { }
            Scaffold.SpeechService.Say(
                $"{DisplayTopic(stop.Keyword)}, {_topic + 1} of {stops.Count}.", "Dialogue");
        }

        /// <summary>The pane's own topic enumeration: toolTipWords in layout
        /// order, consecutive words of one keyword grouped to a single stop
        /// (multi-word topics render as several Word elements).</summary>
        private static List<TopicStop> BuildStops(object gui)
        {
            var stops = new List<TopicStop>();
            try
            {
                object pane = Seams.GUIControl_sceneDescription?.GetValue(gui);
                if (pane == null || Seams.UITextBlock_toolTipWords == null) return stops;
                var words = Seams.UITextBlock_toolTipWords.GetValue(pane) as System.Collections.IEnumerable;
                if (words == null) return stops;
                string prev = null;
                foreach (object word in words)
                {
                    if (word == null) continue;
                    if (_wordType != word.GetType())
                    {
                        _wordType = word.GetType();
                        _wordHighlight = AccessTools.Field(_wordType, "highlightWord");
                    }
                    string key = _wordHighlight?.GetValue(word) as string;
                    if (string.IsNullOrEmpty(key)) { prev = null; continue; }
                    if (key != prev) stops.Add(new TopicStop { Word = word, Keyword = key });
                    prev = key;
                }
            }
            catch { }
            return stops;
        }

        private static int CountTopicsIn(object gui, string sentence)
        {
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int count = 0;
                foreach (var stop in BuildStops(gui))
                {
                    if (!seen.Add(stop.Keyword)) continue;
                    if (sentence.IndexOf(stop.Keyword, StringComparison.OrdinalIgnoreCase) >= 0) count++;
                }
                return count;
            }
            catch { return 0; }
        }

        /// <summary>Speech casing: the matched keyword may be the ALL-CAPS
        /// variant (the pane uppercases lore words) — un-shout it (transcode,
        /// don't curate).</summary>
        private static string DisplayTopic(string keyword)
        {
            bool anyLower = false;
            foreach (char c in keyword) if (char.IsLower(c)) { anyLower = true; break; }
            if (anyLower) return keyword;
            string lower = keyword.ToLowerInvariant();
            var sb = new System.Text.StringBuilder(lower.Length);
            bool boundary = true;
            foreach (char c in lower)
            {
                sb.Append(boundary && char.IsLetter(c) ? char.ToUpperInvariant(c) : c);
                boundary = !char.IsLetter(c);
            }
            return sb.ToString();
        }

        // ---- Prose ----

        /// <summary>Node change detection: same state, new prose (a numeric
        /// press mounts a new node). Indices die with the old document; text
        /// mode exits silently — the node's own auto-read is the anchor.</summary>
        private static void SyncProse(object gui)
        {
            string raw = ProseRaw(gui);
            if (string.Equals(raw, _lastProse, StringComparison.Ordinal)) return;
            _lastProse = raw;
            _sentence = -1;
            _topic = -1;
            if (_textMode) _textMode = false;
        }

        private static string ProseRaw(object gui)
        {
            try
            {
                object pane = Seams.GUIControl_sceneDescription?.GetValue(gui);
                if (pane == null || Seams.UITextBlock_content == null) return null;
                return Seams.UITextBlock_content.GetValue(pane) as string;
            }
            catch { return null; }
        }

        private static List<string> Sentences(object gui)
        {
            var list = new List<string>();
            string raw = ProseRaw(gui);
            if (string.IsNullOrWhiteSpace(raw)) return list;
            string cleaned = Patches.TextCleaner.CleanText(raw);
            if (string.IsNullOrWhiteSpace(cleaned)) return list;
            foreach (var s in Composer.SplitSentences(cleaned)) list.Add(s);
            return list;
        }

        // ---- Small helpers ----

        private static bool FocusAtTopOption(object gui)
        {
            try
            {
                object buttons = Seams.GUIControl_numericButtons?.GetValue(gui);
                if (buttons == null || Seams.UICanvas_getCurrentSelectedButtonIndex == null) return false;
                object idx = Seams.UICanvas_getCurrentSelectedButtonIndex.Invoke(buttons, null);
                return idx is int i && i == 0;
            }
            catch { return false; }
        }

        private static void ParkMouseOnPane(object gui)
        {
            try
            {
                object pane = Seams.GUIControl_sceneDescription?.GetValue(gui);
                if (pane != null)
                    Seams.GUIControl_setMouseToUIElement?.Invoke(gui, new object[] { pane, 8, -8 });
            }
            catch { }
        }

        private static void ClearTooltip()
        {
            try { Seams.ToolTipPrinter_clearToolTip?.Invoke(null, null); }
            catch { }
        }
    }
}

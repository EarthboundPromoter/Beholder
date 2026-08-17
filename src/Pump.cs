using System;
using UnityEngine;

namespace SkaldAccessibility
{
    /// <summary>
    /// The timing spine (build-plan WP3, lineage: Vantage Pump.cs / OAG approved
    /// architecture). Harmony hooks NOTE what fired and nothing else; all reads and
    /// composition happen here, once per frame, at LateUpdate — after the game's own
    /// update has settled. Frame-guarded, exception-proof: the pump never dies.
    ///
    /// Streams (added per work package):
    ///   WP3: state clock — StateChangePatch notes the StateControl instance;
    ///        the drain reads currentState (game truth, no mod mirror) and diffs
    ///        its type name once at the clock. Replaces the per-frame PollState
    ///        poller and its FindObjectOfType hunt.
    ///   WP4 (planned): selection join. WP5 (planned): content sources.
    /// </summary>
    public static class Pump
    {
        // All game reflection handles live in the WP8 Seams registry.

        // ---- State stream (noted by StateChangePatch; latest wins) ----
        private static object _stateControl;
        private static string _lastStateName;

        // ---- Selection stream (noted by SelectionJoinPatch; latest wins) ----
        private static object _pendingSelection;
        private static object _selControl;   // drain-side diff record: which control
        private static int _selIndex = -1;   // ...and which index last spoke/settled

        // ---- Content stream (noted by ContentSpeechPatch; latest wins PER SOURCE) ----
        private struct ContentNote { public string Raw; public bool Interrupt; }
        private static readonly System.Collections.Generic.Dictionary<string, ContentNote> _pendingContent
            = new System.Collections.Generic.Dictionary<string, ContentNote>();
        private static readonly System.Collections.Generic.Dictionary<string, string> _lastContent
            = new System.Collections.Generic.Dictionary<string, string>();

        // ---- Popup stream (noted by PopupAnnouncePatch; latest wins) ----
        private static object _pendingPopup;

        // ---- Combat log batch (accumulates within the frame) ----
        private static readonly System.Collections.Generic.List<string> _pendingCombatLog
            = new System.Collections.Generic.List<string>();

        // ---- Bark batch (accumulates within the frame) ----
        private static readonly System.Collections.Generic.List<string> _pendingBarks
            = new System.Collections.Generic.List<string>();

        // ---- Slider value stream (noted by SliderArrowPatch after an adjust) ----
        private static object _pendingSliderValue;

        // ---- List-edge stream (noted by the B4 edge observer; latest wins) ----
        private static object _pendingEdgeList;
        private static string _pendingEdgePreLine;
        private static string _pendingEdgeText;

        // ---- Slider arrow-flip stream (noted by ArrowFlipJoin; latest wins) ----
        private static object _pendingArrowFlip;

        // ---- Canvas-switch stream (noted by CanvasSwitchPatch; latest wins) ----
        private static object _pendingCanvasSwitch;

        // ---- Travel stream (noted by the WP11 course joins; latest wins) ----
        private static string _pendingTravel;

        // ---- List-selection stream (noted by ListSelectionPatch; latest wins) ----
        private static object _pendingListSelection;
        private static object _lastSelList;   // drain-side diff record: which list
        private static object _lastSelObject; // ...and which current object last spoke
        private static string _yellowTag;     // C64Color.YELLOW_TAG value — the game's
                                              // rendered marker for the current row
                                              // (lazy read via Seams, post-ready)

        private static int _lastFrame = -1;

        /// <summary>Note-only: called from the setState postfix. The game calls
        /// setState every frame from StateControl.update(), so this doubles as the
        /// game-owned clock — every currentState write path (including the direct
        /// assignments inside setState and the boot-time constructor) is visible to
        /// the drain's diff at worst one frame later.</summary>
        public static void NoteStateControl(object stateControl)
        {
            _stateControl = stateControl;
        }

        /// <summary>Live game-truth read of the active StateBase — never a cached
        /// mod-side copy of the state itself.</summary>
        public static object CurrentStateObject()
        {
            var sc = _stateControl;
            if (sc == null || Seams.StateControl_currentState == null) return null;
            return Seams.StateControl_currentState.GetValue(sc);
        }

        /// <summary>Note-only: called from the setCurrentSelectedButton postfix.
        /// Latest wins within a frame; the drain reads the FINAL index, so
        /// set-then-clamp sequences resolve to the settled value.</summary>
        public static void NoteSelection(object control)
        {
            _pendingSelection = control;
        }

        /// <summary>Note-only: latest content per source wins within a frame — a
        /// screen build that calls a setter dozens of times collapses to one
        /// pending value at the clock, instead of being absorbed by a dictionary
        /// at the hook (the WP5 point).</summary>
        public static void NoteContent(string source, string raw, bool interrupt)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            _pendingContent[source] = new ContentNote { Raw = raw, Interrupt = interrupt };

            // Panel-class sources feed the review layer's capture (raw, with
            // markup — the tag grammar is the sectioning schema). Latest wins.
            switch (source)
            {
                case "SceneDesc":
                case "SecondaryDesc":
                case "SheetDesc":
                case "Tooltip":
                case "PopupMain":
                case "PopupSecondary":
                case "PopupTertiary":
                    ReviewLayer.NotePanel(raw);
                    break;
            }
        }

        public static void NotePopup(object popup) => _pendingPopup = popup;

        public static void NoteCombatLog(string line)
        {
            if (!string.IsNullOrWhiteSpace(line)) _pendingCombatLog.Add(line);
        }

        public static void NoteBark(string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) _pendingBarks.Add(text);
        }

        public static void NoteSliderValue(object sliderButton) => _pendingSliderValue = sliderButton;

        /// <summary>Note-only: an edge press whose native window-slide is about
        /// to run (bug-ledger B4 as amended). Carries the focused slot's
        /// pre-slide line; the drain diffs after the re-render.</summary>
        public static void NoteEdgeScroll(object list, string preLine, string edgeText)
        {
            _pendingEdgeList = list;
            _pendingEdgePreLine = preLine;
            _pendingEdgeText = edgeText;
        }

        /// <summary>The composed line of a control's current selection, read at
        /// time of use (used by the edge observer's pre-capture).</summary>
        internal static string CurrentLineOf(object control)
        {
            try
            {
                if (Seams.UICanvas_currentSelectedButton == null || control == null) return null;
                int index = (int)Seams.UICanvas_currentSelectedButton.GetValue(control);
                if (index < 0) return null;
                return ComposeSelection(control, index);
            }
            catch { return null; }
        }

        /// <summary>Note-only: the stick-sideways minus/plus flip on slider rows
        /// (fires once per row per press — the game flips the whole control;
        /// latest wins, all rows share the state).</summary>
        public static void NoteSliderArrowFlip(object sliderButton) => _pendingArrowFlip = sliderButton;

        /// <summary>Note-only: a SkaldObjectList selection write (click path or
        /// direct setter). The drain reads the list's current object and speaks
        /// only actual changes.</summary>
        public static void NoteListSelection(object list) => _pendingListSelection = list;

        /// <summary>Note-only: a popup switched its active navigable canvas
        /// (zone crossing — no index write involved).</summary>
        public static void NoteCanvasSwitch(object canvas) => _pendingCanvasSwitch = canvas;

        /// <summary>Note-only: a travel event line from the WP11 course joins
        /// ("Walking, N steps." / "Stopped." / "No path."). Latest wins — a
        /// clear-then-set in one frame (re-route) speaks only the new walk.</summary>
        public static void NoteTravel(string line) => _pendingTravel = line;

        /// <summary>Called from Plugin.LateUpdate. Drain order encodes precedence
        /// (state → popup → content → selection → slider → combat batch → barks);
        /// SpeechService.Tick runs last so anything drained this frame can still
        /// enter the queue ahead of the pump.</summary>
        public static void Drain()
        {
            if (Time.frameCount == _lastFrame) return;
            _lastFrame = Time.frameCount;

            try { DrainState(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:state] {ex.Message}"); }

            try { DrainPopup(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:popup] {ex.Message}"); }

            try { DrainContent(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:content] {ex.Message}"); }

            try { DrainCanvasSwitch(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:canvas] {ex.Message}"); }

            try { DrainSelection(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:sel] {ex.Message}"); }

            try { DrainEdge(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:edge] {ex.Message}"); }

            try { DrainSliderValue(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:slider] {ex.Message}"); }

            try { DrainSliderArrowFlip(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:flip] {ex.Message}"); }

            try { DrainListSelection(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:listsel] {ex.Message}"); }

            try { DrainTravel(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:travel] {ex.Message}"); }

            try { DrainCombatLog(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:combat] {ex.Message}"); }

            try { DrainBarks(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:bark] {ex.Message}"); }

            try { ReviewLayer.MaintainFromDrain(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:review] {ex.Message}"); }

            try { Scaffold.SpeechService.Tick(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:speech] {ex.Message}"); }
        }

        /// <summary>Popup content reads at the drain — end-of-frame settle kills
        /// set-text-after-addPopUp races by construction.</summary>
        private static void DrainPopup()
        {
            object popup = _pendingPopup;
            if (popup == null) return;
            _pendingPopup = null;
            Patches.PopupAnnouncePatch.SpeakPopupTexts(popup);
        }

        /// <summary>One utterance max per content source per frame, spoken only when
        /// the settled value differs from the last drained value for that source.
        /// No ClearAll, no per-hook dictionaries — the diff lives at the clock.</summary>
        private static void DrainContent()
        {
            if (_pendingContent.Count == 0) return;
            foreach (var kv in _pendingContent)
            {
                string source = kv.Key;
                string cleaned = Patches.TextCleaner.CleanText(kv.Value.Raw);
                if (string.IsNullOrWhiteSpace(cleaned)) continue;
                _lastContent.TryGetValue(source, out string prev);
                if (cleaned == prev) continue;
                _lastContent[source] = cleaned;
                if (kv.Value.Interrupt)
                    Scaffold.SpeechService.Say(cleaned, source);
                else
                    Scaffold.SpeechService.SayQueued(cleaned, source);
            }
            _pendingContent.Clear();
        }

        /// <summary>Align a source's diff record with text another mechanism
        /// just spoke (popup arrival reads the same blocks the ctor setters
        /// write — WP8 double-speak guard). A pending note with the SAME text
        /// then dedups away; a genuinely newer value still differs and speaks.</summary>
        internal static void SeedContent(string source, string cleaned)
        {
            if (!string.IsNullOrWhiteSpace(cleaned)) _lastContent[source] = cleaned;
        }

        /// <summary>Combat log lines batch within the frame; identical consecutive
        /// lines collapse to "text, N times" (compress, don't curate). Gated on the
        /// game's own current state — a live read, not a mod-side mode flag.</summary>
        private static void DrainCombatLog()
        {
            if (_pendingCombatLog.Count == 0) return;
            var lines = new System.Collections.Generic.List<string>(_pendingCombatLog);
            _pendingCombatLog.Clear();

            object state = CurrentStateObject();
            string stateName = state?.GetType().Name ?? "";
            if (!stateName.Contains("Combat")) return;

            int i = 0;
            while (i < lines.Count)
            {
                int run = 1;
                while (i + run < lines.Count && lines[i + run] == lines[i]) run++;
                string text = run > 1 ? $"{lines[i]}, {run} times" : lines[i];
                Scaffold.SpeechService.SayQueued(text, "CombatLog");
                i += run;
            }
        }

        private static void DrainBarks()
        {
            if (_pendingBarks.Count == 0) return;
            var barks = new System.Collections.Generic.List<string>(_pendingBarks);
            _pendingBarks.Clear();
            int i = 0;
            while (i < barks.Count)
            {
                int run = 1;
                while (i + run < barks.Count && barks[i + run] == barks[i]) run++;
                string text = run > 1 ? $"{barks[i]}, {run} times" : barks[i];
                Scaffold.SpeechService.SayQueued(text, "Bark");
                i += run;
            }
        }

        /// <summary>Speak the adjusted slider's settled value (noted by the arrow
        /// dispatch after mutation; read at end of frame so the game's own
        /// value-block refresh has landed).</summary>
        private static void DrainSliderValue()
        {
            object btn = _pendingSliderValue;
            if (btn == null) return;
            _pendingSliderValue = null;
            string text = Patches.SliderArrowPatch.ReadSliderRow(btn, valueOnly: true);
            if (text != null) Scaffold.SpeechService.Say(text, "Slider");
        }

        /// <summary>Speak list-selection changes ("Selected: &lt;row&gt;.") — the
        /// game's current-object write, read back at the clock and diffed, so
        /// the click-to-select step of every list sheet is audible (ledger B6).
        /// A first observation speaks too (owner ruling 2026-08-16: settled
        /// state on arrival is always voiced); only repeats stay silent.</summary>
        private static void DrainListSelection()
        {
            object list = _pendingListSelection;
            if (list == null) return;
            _pendingListSelection = null;

            object current = Patches.ListSelectionPatch.CurrentObjectOf(list);
            if (current == null) return;

            if (ReferenceEquals(list, _lastSelList) && ReferenceEquals(current, _lastSelObject)) return;
            _lastSelList = list;
            _lastSelObject = current;

            string name = Patches.ListSelectionPatch.ListNameOf(current);
            if (name == null) return;
            Scaffold.SpeechService.Say($"Selected: {name}.", "Nav");
        }

        /// <summary>Speak which arrow the cursor flipped onto ("Plus." /
        /// "Minus."), read from the game's own flag at drain time.</summary>
        private static void DrainSliderArrowFlip()
        {
            object btn = _pendingArrowFlip;
            if (btn == null) return;
            _pendingArrowFlip = null;
            string side = Patches.SliderArrowPatch.ReadArrowSide(btn);
            if (side == null) return;
            Scaffold.SpeechService.Say(char.ToUpper(side[0]) + side.Substring(1) + ".", "Nav");
        }

        /// <summary>An edge press moves no selection index, so this is the only
        /// speech the press produces. The native window-slide ran during the
        /// game's update; by the drain the page has re-rendered — if the focused
        /// slot's line changed, a new entry slid under focus and IT is the
        /// announcement; if unchanged, the window is at the model's true end
        /// and the edge line speaks. Never a silent press (B4 as amended).</summary>
        private static void DrainEdge()
        {
            object list = _pendingEdgeList;
            if (list == null) return;
            string pre = _pendingEdgePreLine;
            string edgeText = _pendingEdgeText;
            _pendingEdgeList = null;
            _pendingEdgePreLine = null;
            _pendingEdgeText = null;

            string post = CurrentLineOf(list);
            if (post != null && post != pre)
                Scaffold.SpeechService.Say(post, "Nav");
            else
                Scaffold.SpeechService.Say(edgeText, "Nav");
        }

        /// <summary>Speak the arrived canvas's settled focus on a zone crossing —
        /// bypasses the selection dedup (arrival is the event even when the
        /// canvas's index never changed) and supersedes this frame's selection
        /// note. Button rows carry a role prefix so a zone of buttons doesn't
        /// read as bare labels ("Buttons: Default, 1 of 3."). Index mirrors the
        /// game's own bound-on-read clamp (negative → 0).</summary>
        private static void DrainCanvasSwitch()
        {
            object canvas = _pendingCanvasSwitch;
            if (canvas == null) return;
            _pendingCanvasSwitch = null;

            if (Seams.UICanvas_currentSelectedButton == null) return;

            int index = (int)Seams.UICanvas_currentSelectedButton.GetValue(canvas);
            if (index < 0) index = 0;

            string text = ComposeSelection(canvas, index);
            if (text == null) return;

            bool isButtonRow = Seams.UIButtonControlBaseType != null
                && Seams.UIButtonControlBaseType.IsInstanceOfType(canvas);
            Scaffold.SpeechService.Say(isButtonRow ? $"Buttons: {text}." : text, "Nav");

            // Supersede the frame's selection note and align the dedup records
            // so the next real move on this canvas diffs correctly.
            _pendingSelection = null;
            _selControl = canvas;
            _selIndex = index;
            ReviewLayer.OnFocusChanged();
        }

        /// <summary>Speak selection changes once, at the settled end-of-frame value.
        /// A NEW surface speaks its settled focus too (owner ruling 2026-08-16:
        /// always speak the settled focus on state entry / modal arrival — the
        /// screen-init selection write is the entry event; content sources drain
        /// first, so entry reads as content then focus). The drain-clock settle
        /// still collapses build-time write bursts to the one final value.</summary>
        private static void DrainSelection()
        {
            object control = _pendingSelection;
            if (control == null) return;
            _pendingSelection = null;

            if (Seams.UICanvas_currentSelectedButton == null) return;

            int index = (int)Seams.UICanvas_currentSelectedButton.GetValue(control);

            if (ReferenceEquals(control, _selControl) && index == _selIndex) return;
            _selControl = control;
            _selIndex = index;
            ReviewLayer.OnFocusChanged(); // review cursors reset with focus
            if (index < 0) return;

            string text = ComposeSelection(control, index);
            if (text == null) return; // non-conforming control — graceful silence
            Scaffold.SpeechService.Say(text, "Nav");
        }

        /// <summary>Rendered-text composition for the focused element. Numeric-class
        /// button rows (SheetButtonControl / NumericButtonControl / MenuButtonControl
        /// — the game's 1-9 keyboard classes) keep their leading "N:" shortcut label
        /// (content, not a browse counter); other lists get a trailing "N of M"
        /// (positional counts trail — RW3 standing rule).</summary>
        private static string ComposeSelection(object control, int index)
        {
            // Selector grids (WP9): cells are image buttons with no rendered
            // text — names resolve from the game's own button-data list.
            if (Seams.UIAbilitySelectorGridType != null && Seams.UIAbilitySelectorGridType.IsInstanceOfType(control))
                return ComposeGridSelection(control, index);

            int count = -1;
            string text = null;
            bool isCurrentListRow = false;

            if (Seams.UIButtonControlBase_getButtonsList != null && Seams.UITextBlock_content != null)
            {
                try
                {
                    var buttons = Seams.UIButtonControlBase_getButtonsList.Invoke(control, null) as System.Collections.IList;
                    if (buttons != null && index >= 0 && index < buttons.Count)
                    {
                        count = buttons.Count;
                        object button = buttons[index];
                        string raw = button != null ? Seams.UITextBlock_content.GetValue(button) as string : null;
                        // List sheets render the SkaldObjectList current object
                        // wrapped in the yellow tag at position 0
                        // (SkaldObjectList.getScrolledStringList) — transcode the
                        // markup into "selected" instead of stripping it (B6).
                        string yellow = YellowTag();
                        isCurrentListRow = yellow != null && raw != null && raw.StartsWith(yellow);
                        if (!string.IsNullOrWhiteSpace(raw) && raw != " ")
                        {
                            string cleaned = Patches.TextCleaner.CleanText(raw);
                            if (!string.IsNullOrWhiteSpace(cleaned))
                                text = (cleaned == "..." || cleaned == "…") ? "dot dot dot" : cleaned;
                        }
                        // Slider rows render header and value in separate text
                        // blocks, not button content — describe as "Header: Value"
                        // and queue the row's full description behind it (WP6;
                        // replaces the deleted hover watcher).
                        if (text == null && button != null)
                        {
                            text = Patches.SliderArrowPatch.ReadSliderRow(button, valueOnly: false);
                            if (text != null)
                                Patches.SliderArrowPatch.QueueDescription(button);
                        }
                    }
                }
                catch { /* not a UIButtonControlBase — fall through */ }
            }

            // Slider controls (UITextSliderControl — the visual-style modal,
            // settings sliders): the scrollable elements are each row's chosen
            // minus/plus arrow, so the buttons-list path never matches. Map the
            // arrow back to its owning row and speak the slider composition
            // (closes the B2-class silence on vertical nav over slider rows).
            if (text == null && Seams.UICanvas_getScrollableElements != null)
            {
                try
                {
                    var elements = Seams.UICanvas_getScrollableElements.Invoke(control, null)
                        as System.Collections.Generic.List<UIElement>;
                    if (elements != null && index >= 0 && index < elements.Count)
                    {
                        object row = Patches.SliderArrowPatch.RowForScrollableElement(control, elements[index]);
                        if (row != null)
                        {
                            count = elements.Count;
                            text = Patches.SliderArrowPatch.ReadSliderRow(row, valueOnly: false);
                            if (text != null)
                                Patches.SliderArrowPatch.QueueDescription(row);
                        }
                    }
                }
                catch { }
            }

            // Image-only elements (feat-tree nodes): read the feat name from the
            // scrollable element's backing object.
            if (text == null && Seams.UICanvas_getScrollableElements != null
                && Seams.FeatNode_feat != null && Seams.Feat_getName != null)
            {
                try
                {
                    var elements = Seams.UICanvas_getScrollableElements.Invoke(control, null)
                        as System.Collections.Generic.List<UIElement>;
                    if (elements != null && index >= 0 && index < elements.Count)
                    {
                        count = elements.Count;
                        object node = elements[index];
                        if (node != null && Seams.FeatNodeType != null && Seams.FeatNodeType.IsInstanceOfType(node))
                        {
                            object feat = Seams.FeatNode_feat.GetValue(node);
                            string name = feat != null ? Seams.Feat_getName.Invoke(feat, null) as string : null;
                            if (!string.IsNullOrWhiteSpace(name))
                                text = Patches.TextCleaner.CleanText(name);
                        }
                    }
                }
                catch { }
            }

            if (text == null) return null;

            // Numeric-class rows by registry-audited type identity (WP8) — a
            // rename shows up in the boot report instead of silently demoting
            // the row to a browse counter.
            Type controlType = control.GetType();
            bool numericClass = controlType == Seams.SheetButtonControlType
                || controlType == Seams.NumericButtonControlType
                || controlType == Seams.MenuButtonControlType;

            if (numericClass) return $"{index + 1}: {text}";
            if (isCurrentListRow) text = $"{text}, selected";
            if (count > 1) return $"{text}, {index + 1} of {count}";
            return text;
        }

        /// <summary>Grid-cell composition (WP9): name from the game's own
        /// button-data list (index-aligned by construction), trailing count.
        /// The native hover pipeline echoes the same name into
        /// setSecondaryDescription a frame later — seed that source's diff so
        /// the echo dedups (the WP8 popup pattern); a genuinely different
        /// hover text still speaks.</summary>
        private static string ComposeGridSelection(object grid, int index)
        {
            try
            {
                var elements = Seams.UICanvas_getScrollableElements?.Invoke(grid, null)
                    as System.Collections.ICollection;
                int count = elements != null ? elements.Count : 0;
                if (count == 0 || index < 0 || index >= count) return null;

                string raw = Patches.GridNavigationPatch.NameAt(grid, index);
                string name = string.IsNullOrWhiteSpace(raw) ? null : Patches.TextCleaner.CleanText(raw);
                if (string.IsNullOrWhiteSpace(name)) return null;

                SeedContent("SecondaryDesc", name);
                return count > 1 ? $"{name}, {index + 1} of {count}" : name;
            }
            catch { return null; }
        }

        /// <summary>The game's own current-row marker, read once from
        /// C64Color.YELLOW_TAG via the Seams handle (colors load with game
        /// data; composition only runs post-ready, so the lazy value read is
        /// safe here — never at Awake).</summary>
        private static string YellowTag()
        {
            if (_yellowTag != null) return _yellowTag.Length == 0 ? null : _yellowTag;
            _yellowTag = Seams.TagValue(Seams.C64_YellowTag) ?? "";
            if (_yellowTag.Length == 0)
                Plugin.Logger?.LogWarning("[Pump:sel] C64Color.YELLOW_TAG unavailable — selected-row state unvoiced");
            return _yellowTag.Length == 0 ? null : _yellowTag;
        }

        private static void DrainTravel()
        {
            string line = _pendingTravel;
            if (line == null) return;
            _pendingTravel = null;
            Scaffold.SpeechService.SayQueued(line, "Nav");
        }

        private static void DrainState()
        {
            object state = CurrentStateObject();
            if (state == null) return;
            string name = state.GetType().Name;
            if (name == _lastStateName) return;
            _lastStateName = name;
            ReviewLayer.OnStateTransition();    // review never survives a state change
            OverlandCursor.OnStateTransition(); // neither does the cursor or its list
            GameStateTracker.OnStateChanged(name, state);
        }

        /// <summary>The focused element's composed line, for the review layer's
        /// close re-anchor. Null when no selection is known.</summary>
        internal static string CurrentFocusLine()
        {
            try
            {
                if (_selControl == null || _selIndex < 0) return null;
                return ComposeSelection(_selControl, _selIndex);
            }
            catch { return null; }
        }
    }
}

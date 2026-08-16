using System;
using System.Reflection;
using HarmonyLib;
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
        // ---- State stream (noted by StateChangePatch; latest wins) ----
        private static object _stateControl;
        private static FieldInfo _currentStateField;
        private static string _lastStateName;

        // ---- Selection stream (noted by SelectionJoinPatch; latest wins) ----
        private static object _pendingSelection;
        private static object _selControl;   // drain-side diff record: which control
        private static int _selIndex = -1;   // ...and which index last spoke/settled
        private static FieldInfo _selIndexField;            // UICanvas.currentSelectedButton
        private static MethodInfo _getButtonsListMethod;    // UIButtonControlBase.getButtonsList
        private static MethodInfo _getScrollableElementsMethod; // UICanvas.getScrollableElements
        private static FieldInfo _contentField;             // UITextBlock.content
        private static FieldInfo _featField;                // UIFeatTree+FeatTreeCollection+Node.feat
        private static MethodInfo _featGetNameMethod;       // FeatContainer+Feat.getName
        private static bool _selReflectionReady;

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

        // ---- List-edge stream (noted by the B4 edge clamp; latest wins) ----
        private static string _pendingEdge;

        // ---- Slider arrow-flip stream (noted by ArrowFlipJoin; latest wins) ----
        private static object _pendingArrowFlip;

        // ---- List-selection stream (noted by ListSelectionPatch; latest wins) ----
        private static object _pendingListSelection;
        private static object _lastSelList;   // drain-side diff record: which list
        private static object _lastSelObject; // ...and which current object last spoke
        private static string _yellowTag;     // C64Color.YELLOW_TAG — the game's
                                              // rendered marker for the current row

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
            if (sc == null) return null;
            if (_currentStateField == null)
            {
                _currentStateField = AccessTools.Field(sc.GetType(), "currentState");
                if (_currentStateField == null) return null;
            }
            return _currentStateField.GetValue(sc);
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

        /// <summary>Note-only: called from the edge-clamp prefix when a press
        /// lands on a true list edge (bug-ledger B4 ruling — clamp, no wrap,
        /// speak the edge).</summary>
        public static void NoteEdge(string text) => _pendingEdge = text;

        /// <summary>Note-only: the stick-sideways minus/plus flip on slider rows
        /// (fires once per row per press — the game flips the whole control;
        /// latest wins, all rows share the state).</summary>
        public static void NoteSliderArrowFlip(object sliderButton) => _pendingArrowFlip = sliderButton;

        /// <summary>Note-only: a SkaldObjectList selection write (click path or
        /// direct setter). The drain reads the list's current object and speaks
        /// only actual changes.</summary>
        public static void NoteListSelection(object list) => _pendingListSelection = list;

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

            try { DrainCombatLog(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:combat] {ex.Message}"); }

            try { DrainBarks(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:bark] {ex.Message}"); }

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
        /// First observation of a list settles silently, mirroring the
        /// selection join's new-surface rule.</summary>
        private static void DrainListSelection()
        {
            object list = _pendingListSelection;
            if (list == null) return;
            _pendingListSelection = null;

            object current = Patches.ListSelectionPatch.CurrentObjectOf(list);
            if (current == null) return;

            if (!ReferenceEquals(list, _lastSelList))
            {
                _lastSelList = list;
                _lastSelObject = current;
                return;
            }
            if (ReferenceEquals(current, _lastSelObject)) return;
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

        /// <summary>An edge press moves no selection, so this is the only speech
        /// the press produces; immediate priority, same source as navigation.</summary>
        private static void DrainEdge()
        {
            string edge = _pendingEdge;
            if (edge == null) return;
            _pendingEdge = null;
            Scaffold.SpeechService.Say(edge, "Nav");
        }

        /// <summary>Speak selection changes once, at the settled end-of-frame value.
        /// The FIRST note for a control instance records silently — screen/popup
        /// entry speech belongs to the content sources, and the game resets
        /// selection to 0 on build (this replaces the old init-suppression
        /// windows). Subsequent index changes on the same control speak.</summary>
        private static void DrainSelection()
        {
            object control = _pendingSelection;
            if (control == null) return;
            _pendingSelection = null;

            if (!_selReflectionReady) InitSelectionReflection();
            if (_selIndexField == null) return;

            int index = (int)_selIndexField.GetValue(control);

            if (!ReferenceEquals(control, _selControl))
            {
                // New surface: settle silently.
                _selControl = control;
                _selIndex = index;
                return;
            }
            if (index == _selIndex) return;
            _selIndex = index;
            if (index < 0) return;

            string text = ComposeSelection(control, index);
            if (text == null) return; // non-conforming control — graceful silence
            Scaffold.SpeechService.Say(text, "Nav");
        }

        private static void InitSelectionReflection()
        {
            _selReflectionReady = true;
            var uiCanvas = AccessTools.TypeByName("UICanvas");
            var buttonBase = AccessTools.TypeByName("UIButtonControlBase");
            if (uiCanvas != null)
            {
                _selIndexField = AccessTools.Field(uiCanvas, "currentSelectedButton");
                _getScrollableElementsMethod = AccessTools.Method(uiCanvas, "getScrollableElements");
            }
            if (buttonBase != null)
                _getButtonsListMethod = AccessTools.Method(buttonBase, "getButtonsList");
            _contentField = AccessTools.Field(AccessTools.TypeByName("UITextBlock"), "content");
            var nodeType = AccessTools.TypeByName("UIFeatTree+FeatTreeCollection+Node");
            if (nodeType != null) _featField = AccessTools.Field(nodeType, "feat");
            var featType = AccessTools.TypeByName("FeatContainer+Feat");
            if (featType != null) _featGetNameMethod = AccessTools.Method(featType, "getName");
            if (_selIndexField == null)
                Plugin.Logger?.LogError("[Pump:sel] currentSelectedButton field not found — selection speech disabled");
        }

        /// <summary>Rendered-text composition for the focused element. Numeric-class
        /// button rows (SheetButtonControl / NumericButtonControl / MenuButtonControl
        /// — the game's 1-9 keyboard classes) keep their leading "N:" shortcut label
        /// (content, not a browse counter); other lists get a trailing "N of M"
        /// (positional counts trail — RW3 standing rule).</summary>
        private static string ComposeSelection(object control, int index)
        {
            int count = -1;
            string text = null;
            bool isCurrentListRow = false;

            if (_getButtonsListMethod != null && _contentField != null)
            {
                try
                {
                    var buttons = _getButtonsListMethod.Invoke(control, null) as System.Collections.IList;
                    if (buttons != null && index >= 0 && index < buttons.Count)
                    {
                        count = buttons.Count;
                        object button = buttons[index];
                        string raw = button != null ? _contentField.GetValue(button) as string : null;
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

            // Image-only elements (feat-tree nodes): read the feat name from the
            // scrollable element's backing object.
            if (text == null && _getScrollableElementsMethod != null && _featField != null && _featGetNameMethod != null)
            {
                try
                {
                    var elements = _getScrollableElementsMethod.Invoke(control, null)
                        as System.Collections.Generic.List<UIElement>;
                    if (elements != null && index >= 0 && index < elements.Count)
                    {
                        count = elements.Count;
                        object node = elements[index];
                        if (node != null && _featField.DeclaringType.IsInstanceOfType(node))
                        {
                            object feat = _featField.GetValue(node);
                            string name = feat != null ? _featGetNameMethod.Invoke(feat, null) as string : null;
                            if (!string.IsNullOrWhiteSpace(name))
                                text = Patches.TextCleaner.CleanText(name);
                        }
                    }
                }
                catch { }
            }

            if (text == null) return null;

            string typeName = control.GetType().Name;
            bool numericClass = typeName == "SheetButtonControl"
                || typeName == "NumericButtonControl"
                || typeName == "MenuButtonControl";

            if (numericClass) return $"{index + 1}: {text}";
            if (isCurrentListRow) text = $"{text}, selected";
            if (count > 1) return $"{text}, {index + 1} of {count}";
            return text;
        }

        /// <summary>The game's own current-row marker, read once from
        /// C64Color.YELLOW_TAG (colors load with game data; composition only
        /// runs post-ready, so the type is long initialized).</summary>
        private static string YellowTag()
        {
            if (_yellowTag != null) return _yellowTag.Length == 0 ? null : _yellowTag;
            try
            {
                var prop = AccessTools.Property(AccessTools.TypeByName("C64Color"), "YELLOW_TAG");
                _yellowTag = prop?.GetValue(null, null) as string ?? "";
            }
            catch { _yellowTag = ""; }
            if (_yellowTag.Length == 0)
                Plugin.Logger?.LogWarning("[Pump:sel] C64Color.YELLOW_TAG unavailable — selected-row state unvoiced");
            return _yellowTag.Length == 0 ? null : _yellowTag;
        }

        private static void DrainState()
        {
            object state = CurrentStateObject();
            if (state == null) return;
            string name = state.GetType().Name;
            if (name == _lastStateName) return;
            _lastStateName = name;
            GameStateTracker.OnStateChanged(name, state);
        }
    }
}

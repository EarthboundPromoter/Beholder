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
        private static object _selElement;   // ...and which element sat at that index
                                             // (feat lateral moves change the element
                                             // set under a constant index — CC report
                                             // build 2026-08-16)

        // A canvas's FIRST focus utterance in a state is an arrival observation
        // (engine init writes focus after content — BaseMenuState ctor, list
        // inits), so it queues behind the entry read; only later events on an
        // already-heard canvas are user moves that own the interrupt (owner
        // policy 2026-08-17: joins respect engine init order). Reference
        // identity — game UI objects must not be compared by value.
        private static readonly System.Collections.Generic.HashSet<object> _spokenCanvases
            = new System.Collections.Generic.HashSet<object>(new RefComparer());
        // B6 two-cursor lists carry BOTH joins on the same canvas object
        // (funnel focus and current-object), and each is its own arrival —
        // a shared set would let the entry focus line consume the first-touch
        // and the same-frame "Selected:" init write would interrupt after all.
        private static readonly System.Collections.Generic.HashSet<object> _spokenListSelections
            = new System.Collections.Generic.HashSet<object>(new RefComparer());
        // Player-nav stamp (noted by the SkaldIO direction-read postfixes,
        // owner ruling 2026-08-17): a frame in which an option-selection
        // direction read answered true is a frame the player navigated —
        // focus lines it produces own the interrupt even on first
        // observation. Confirms never stamp; mounts stay under entry
        // discipline.
        private static int _playerNavFrame = -1;
        public static void NotePlayerNav() => _playerNavFrame = Time.frameCount;
        private sealed class RefComparer : System.Collections.Generic.IEqualityComparer<object>
        {
            bool System.Collections.Generic.IEqualityComparer<object>.Equals(object a, object b)
                => ReferenceEquals(a, b);
            int System.Collections.Generic.IEqualityComparer<object>.GetHashCode(object o)
                => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        // ---- Content stream (noted by ContentSpeechPatch; latest wins PER SOURCE) ----
        private struct ContentNote { public string Raw; public bool Interrupt; }
        private static readonly System.Collections.Generic.Dictionary<string, ContentNote> _pendingContent
            = new System.Collections.Generic.Dictionary<string, ContentNote>();
        private static readonly System.Collections.Generic.Dictionary<string, string> _lastContent
            = new System.Collections.Generic.Dictionary<string, string>();
        private static readonly System.Collections.Generic.Dictionary<string, string> _lastPanelForwarded
            = new System.Collections.Generic.Dictionary<string, string>();

        // ---- Popup stream: top-of-stack watch (CC report build 2026-08-16).
        //      The game keeps a popup STACK with multiple top-changing paths
        //      (add, dismiss-revealing-the-one-beneath, frame-late UI builds) and
        //      only the add has a single write point — so the drain reads
        //      PopUpControl.getCurrentPopUp(), the game's own authoritative
        //      current-popup accessor, and announces identity changes. ----
        private static object _announcedPopup;      // top-of-stack last announced
        private static bool _popupWasUp;            // for stack-empty cleanup
        private static bool _popupSpokeThisFrame;   // demotes same-frame nav lines
                                                    // to queued so the popup body
                                                    // is never cut by its own
                                                    // button row (F1)

        // ---- Combat log batch (accumulates within the frame) ----
        private static readonly System.Collections.Generic.List<string> _pendingCombatLog
            = new System.Collections.Generic.List<string>();

        // ---- Bark batch (accumulates within the frame) ----
        private static readonly System.Collections.Generic.List<string> _pendingBarks
            = new System.Collections.Generic.List<string>();

        // ---- Slider value stream (noted by SliderArrowPatch after an adjust) ----
        private static object _pendingSliderValue;

        // ---- Attribute-editor flip stream (noted by EditorFlipPatch; the
        //      sheet instance — the drain reads its entry flag at the clock) ----
        private static object _pendingEditorFlip;

        // ---- Inventory hover stream (noted by InventoryHoverPatch during the
        //      segment's own update pass; the hovered cell is the surface's
        //      focus truth — owner direction 2026-08-17) ----
        private static object _pendingInvHoverSegment;
        private static int _pendingInvHoverRow = -1, _pendingInvHoverCol = -1;
        private static object _invHoverSegment;   // drain-side diff record
        private static int _invHoverRow = -1, _invHoverCol = -1;

        // ---- Inventory filter stream (noted by FilterAnnouncePatch on the
        //      setFilterByIndex choke; the drain diffs the settled index) ----
        private static object _pendingFilterControl;
        private static object _lastFilterControl;
        private static int _lastFilterIndex = -1;
        private static bool _filterSpokeThisFrame;

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

        // ---- Character-creation point streams (CC report build 2026-08-16;
        //      noted by PointAllocationPatch; latest wins per slot) ----
        private struct PointPress { public bool IsAttribute; public bool IsPlus; public object Row; }
        private static PointPress? _pendingPress;
        private static int? _pendingAttrPool;
        private static int? _pendingSkillPool;
        private static int _lastAttrPool = int.MinValue;   // int.MinValue = unseen
        private static int _lastSkillPool = int.MinValue;  // (reset per state change)
        private static object _pendingFeatRank;            // feat whose rank changed
        private static object _lastFeatTree;               // tree-crossing prefix record
        private static readonly System.Collections.Generic.List<object> _pendingRefunds
            = new System.Collections.Generic.List<object>(); // cascade-refunded feats (this frame)
        // The legality cascade drains ONE rank per feat per FRAME, so a
        // multi-rank refund arrives across consecutive frames (adversarial
        // review F4) — tally per feat and speak once after a quiet frame,
        // holding the FeatPoints trailer until the tally settles.
        private static readonly System.Collections.Generic.Dictionary<object, int> _refundTally
            = new System.Collections.Generic.Dictionary<object, int>();
        private static int _refundQuietFrames;
        private static bool RefundSettling => _refundTally.Count > 0;

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
            // TP1: the review-panel forward moved to the drain (settled reads
            // — a value superseded within the frame never stomps the buffer,
            // and strip provenance needs the drain-clock recompose anyway).
        }

        /// <summary>Note-only: an attribute/skill plus/minus press surfaced by the
        /// game's own pressed-object wrappers (UIAttributeEditorSheet). Fires
        /// pre-mutation; the drain reads post-mutation truth.</summary>
        public static void NotePointPress(bool isAttribute, bool isPlus, object row)
            => _pendingPress = new PointPress { IsAttribute = isAttribute, IsPlus = isPlus, Row = row };

        // ---- Tooltip auto-dismiss (owner ruling 2026-08-19) ----
        private static int _tooltipRaisedFrame = -1;

        /// <summary>Note-only: a tooltip was raised this frame (the single
        /// setToolTip choke). The drain clears it within the two-frame grace
        /// window, before its hover flag latches the global UIElement yield.</summary>
        public static void NoteTooltipRaised() => _tooltipRaisedFrame = Time.frameCount;

        private static void DrainTooltipDismiss()
        {
            if (_tooltipRaisedFrame < 0) return;
            if (Time.frameCount - _tooltipRaisedFrame > 2) { _tooltipRaisedFrame = -1; return; }
            if (!PanelPolicy.TooltipAutoDismiss) { _tooltipRaisedFrame = -1; return; }
            try
            {
                if (Seams.ToolTipPrinter_hasToolTip != null
                    && (bool)Seams.ToolTipPrinter_hasToolTip.Invoke(null, null))
                    Seams.ToolTipPrinter_clearToolTip?.Invoke(null, null);
            }
            catch { }
            _tooltipRaisedFrame = -1;
        }

        /// <summary>Note-only: a points-pool render write (setAttributePoints /
        /// setSkillPoints — fires every frame with the settled value).</summary>
        public static void NotePointPool(bool isAttribute, int value)
        {
            if (isAttribute) _pendingAttrPool = value;
            else _pendingSkillPool = value;
        }

        /// <summary>Note-only: a feat's rank actually changed under a buy/refund
        /// press (rank diffed pre/post by the hook).</summary>
        public static void NoteFeatRank(object feat) => _pendingFeatRank = feat;

        /// <summary>Note-only: a staged rank was silently drained by the
        /// legality cascade (batch — one press can refund several feats).</summary>
        public static void NoteFeatRefund(object feat)
        {
            if (feat != null && !_pendingRefunds.Contains(feat)) _pendingRefunds.Add(feat);
        }

        // ---- Zone-label slot (sheet-grid crossings): prepended to the next
        //      selection line so "Maneuver Abilities. Cleave, 1 of 5" arrives
        //      as one utterance. Latest wins; consumed on speak. ----
        private static string _pendingZoneLabel;
        public static void NoteZoneLabel(string label)
        {
            _pendingZoneLabel = label;
            Scaffold.Log.Debug("Mode", $"zone={label}");   // L7: zone truth, one grep
        }

        // Overland status strip: first value after a state transition settles
        // silently (owner ruling 2026-08-17 — quiet on load).
        /// <summary>The panel-class sources — captures the review buffer holds,
        /// forwarded at the drain with provenance (TP1).</summary>
        private static readonly string[] PanelSources =
            { "SceneDesc", "SecondaryDesc", "SheetDesc", "Tooltip", "PopupMain", "PopupSecondary", "PopupTertiary" };

        // ---- Locality hold (owner ruling 2026-08-17): dialogue takes
        //      precedence over locality furniture — headers noted (or already
        //      queued) while a scene-family state is up are HELD, latest per
        //      source, and released queued when the dialogue ends. A hold and
        //      flush, never a loss. ----
        private static readonly System.Collections.Generic.Dictionary<string, string> _heldFurniture
            = new System.Collections.Generic.Dictionary<string, string>();
        private static bool _inSceneFamily;
        private static readonly string[] FurnitureSources = { "PrimaryHeader", "BigHeader" };

        private static bool IsSceneFamily(object state)
            => state != null && Seams.SceneBaseStateType != null
               && Seams.SceneBaseStateType.IsInstanceOfType(state);

        public static void NoteCombatLog(string line)
        {
            if (!string.IsNullOrWhiteSpace(line)) _pendingCombatLog.Add(line);
        }

        public static void NoteBark(string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) _pendingBarks.Add(text);
        }

        /// <summary>Remove one pending bark by exact text — used by a composed
        /// line that ABSORBS the game's raw bark (the CP1 disengage ruling:
        /// one utterance, never two). Runs in the spine drain, before
        /// DrainBarks. False when no such bark is pending this frame — the
        /// caller degrades to letting the native bark speak.</summary>
        internal static bool StealBark(string exact)
        {
            for (int i = 0; i < _pendingBarks.Count; i++)
            {
                if (_pendingBarks[i] == exact)
                {
                    _pendingBarks.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Remove pending combat-log lines containing a fragment
        /// (case-insensitive) — the log half of a composed absorption. Returns
        /// the number removed.</summary>
        internal static int StealCombatLogContaining(string fragment)
        {
            int removed = 0;
            for (int i = _pendingCombatLog.Count - 1; i >= 0; i--)
            {
                if (_pendingCombatLog[i].IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _pendingCombatLog.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
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

        /// <summary>Note-only: an attribute-editor sheet flipped its plus/minus
        /// row cursor (A/D sideways). The drain reads the settled flag.</summary>
        public static void NoteEditorFlip(object sheet) => _pendingEditorFlip = sheet;

        /// <summary>Note-only: the virtual cursor sits on an inventory cell
        /// (found during the segment's own update). Latest wins; only noted
        /// when a hovered cell exists, so an idle segment never clobbers
        /// another's hover.</summary>
        public static void NoteInventoryHover(object segment, int row, int col)
        {
            _pendingInvHoverSegment = segment;
            _pendingInvHoverRow = row;
            _pendingInvHoverCol = col;
        }

        /// <summary>Note-only: an inventory filter index write (Ctrl cycle or
        /// filter-button click). The drain diffs the settled index.</summary>
        public static void NoteFilterChange(object filterControl) => _pendingFilterControl = filterControl;

        /// <summary>Worn-zone crossings (owner build 2026-08-17): the zone
        /// label — the game's own "Items Worn" / "Party Inventory" header text
        /// — rides the selection note; a pending hover note from earlier in
        /// the frame dies here so it cannot outvoice the crossing.</summary>
        internal static void NoteWornZoneEntry(object sheet)
        {
            _pendingInvHoverSegment = null;
            _pendingInvHoverRow = _pendingInvHoverCol = -1;
            NoteZoneLabel("Items Worn");
            NoteSelection(sheet);
        }

        internal static void NoteWornZoneExit(object sheet)
        {
            _pendingInvHoverSegment = null;
            _pendingInvHoverRow = _pendingInvHoverCol = -1;
            NoteZoneLabel("Party Inventory");
            NoteSelection(sheet);
        }

        /// <summary>Called from Plugin.LateUpdate. Drain order encodes precedence
        /// (state → popup → content → selection → slider → combat batch → barks);
        /// SpeechService.Tick runs last so anything drained this frame can still
        /// enter the queue ahead of the pump.</summary>
        public static void Drain()
        {
            if (Time.frameCount == _lastFrame) return;
            _lastFrame = Time.frameCount;
            _popupSpokeThisFrame = false;
            _filterSpokeThisFrame = false;

            try { DrainState(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:state", ex.Message); }

            try { DrainPopup(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:popup", ex.Message); }

            try { DrainPoints(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:points", ex.Message); }

            try { DrainContent(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:content", ex.Message); }

            try { DrainTooltipDismiss(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:tipclear", ex.Message); }

            try { DrainCanvasSwitch(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:canvas", ex.Message); }

            try { DrainFilterChange(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:filter", ex.Message); }

            try { DrainSelection(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:sel", ex.Message); }

            try { DrainInventoryHover(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:invhover", ex.Message); }

            try { DrainEdge(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:edge", ex.Message); }

            try { DrainSliderValue(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:slider", ex.Message); }

            try { DrainSliderArrowFlip(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:flip", ex.Message); }

            try { DrainEditorFlip(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:editorflip", ex.Message); }

            try { DrainListSelection(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:listsel", ex.Message); }

            try { DrainTravel(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:travel", ex.Message); }

            // CP3 combat cursor: the deferred landing line speaks from settled
            // truth (the game's update re-hovered and recomputed the course
            // since the nudge). Before the spine so a landing interrupt lands
            // ahead of the same frame's queued combat cues.
            try { CombatCursor.DrainSpeak(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:ccursor", ex.Message); }

            // CP1 turn spine: before DrainCombatLog so a turn boundary always
            // precedes that turn's own log narration (and can steal a pending
            // bark/log line it composed into one utterance — the disengage
            // ruling). The native "Round N" primaryHeader spoke in
            // DrainContent above — round-line-then-turn-line by construction.
            try { CombatSpine.Drain(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:spine", ex.Message); }

            try { DrainCombatLog(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:combat", ex.Message); }

            // Tactical flashes with no log event this frame (Cascade's only
            // channel) — a no-op when the composer already spoke them.
            try { CombatSpine.SpeakOrphanTactical(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:tactical", ex.Message); }

            try { DrainBarks(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:bark", ex.Message); }

            try { ReviewLayer.MaintainFromDrain(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:review", ex.Message); }

            try { Scaffold.SpeechService.Tick(); }
            catch (Exception ex) { Scaffold.Log.Throttled("Pump:speech", ex.Message); }
        }

        /// <summary>The top-of-stack watch: announce whenever the game's own
        /// current-popup identity changes — covers adds, popups revealed by
        /// dismissing the one stacked above them (which never re-fire addPopUp),
        /// and popups that build their UI a frame after the add (retried until
        /// readable). When the stack empties, the popup diff records die with it
        /// so a later popup with identical text is a new event, not a dedup.</summary>
        private static void DrainPopup()
        {
            if (Seams.PopUpControl_getCurrentPopUp == null) return;
            object top = Seams.PopUpControl_getCurrentPopUp.Invoke(null, null);

            if (top == null)
            {
                if (_popupWasUp)
                {
                    _popupWasUp = false;
                    _announcedPopup = null;
                    _lastContent.Remove("PopupMain");
                    _lastContent.Remove("PopupSecondary");
                    _lastContent.Remove("PopupTertiary");
                }
                return;
            }

            _popupWasUp = true;
            if (ReferenceEquals(top, _announcedPopup)) return;

            if (Patches.PopupAnnouncePatch.SpeakPopupTexts(top))
            {
                _announcedPopup = top;
                _popupSpokeThisFrame = true;
            }
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
                // While a multi-frame refund cascade settles, hold its
                // remaining-points trailer — the setter refires every frame,
                // so the settled value re-notes and speaks once, after the
                // refund line (F4).
                if (source == "FeatPoints" && RefundSettling) continue;
                string cleaned = Patches.TextCleaner.CleanText(kv.Value.Raw);
                if (string.IsNullOrWhiteSpace(cleaned)) continue;
                if (source == "PopupTertiary")
                {
                    // PopUpName repaints its tertiary every frame with a blinking
                    // trailing "_" text cursor — normalize it out so the blink
                    // can't defeat the diff and talk over the prompt.
                    cleaned = cleaned.TrimEnd('_').TrimEnd();
                    if (cleaned.Length == 0) continue;
                }

                // TP1 strip provenance (closes ledger B7; replaces the "Time: "
                // shape-sniff): ask the strip's one author. A pure strip routes
                // to the per-fact differ (weather/phase transitions speak under
                // their own configs; clock/position stay silent) and NEVER
                // speaks whole or enters the review buffer; a bump-append
                // composite is split — the residue IS the event text.
                string forwardRaw = kv.Value.Raw;
                // Insurance (TP1 review find 7): if a game update ever removes
                // the getBuffer seam, the recompose can't identify anything and
                // the raw strip would speak in full every step — worse than
                // pre-TP1. The old shape-sniff survives as the missing-seam
                // backstop only: suppress, never fact-diff.
                if (source == "SecondaryDesc" && Seams.DataControl_getBuffer == null
                    && cleaned.StartsWith("Time:"))
                {
                    _lastContent[source] = cleaned;
                    continue;
                }
                if (source == "SecondaryDesc"
                    && PanelPolicy.TryHandleStrip(kv.Value.Raw, out string stripResidue))
                {
                    if (stripResidue == null)
                    {
                        // Pure strip: facts handled; keep the diff record so a
                        // suppressed value can never speak late over other UI.
                        _lastContent[source] = cleaned;
                        continue;
                    }
                    forwardRaw = stripResidue;
                    cleaned = Patches.TextCleaner.CleanText(stripResidue);
                    if (string.IsNullOrWhiteSpace(cleaned))
                    {
                        _lastContent[source] = Patches.TextCleaner.CleanText(kv.Value.Raw);
                        continue;
                    }
                }

                // Panel-class sources feed the review buffer (raw, with markup,
                // WITH provenance — the source tag picks the section map).
                // Genuinely-new only: repaints of an unchanged value must not
                // stomp a newer panel (owner ride 2026-08-17). Popup blocks
                // never forward INDIVIDUALLY (TP1 review find 2 — a single
                // block would truncate the buffer's composite document): a
                // changed block re-composes the WHOLE popup panel instead,
                // blink-normalized inside ComposePanel.
                if (System.Array.IndexOf(PanelSources, source) >= 0)
                {
                    bool popupBlock = source == "PopupMain" || source == "PopupSecondary"
                        || source == "PopupTertiary";
                    if (source == "PopupTertiary")
                        forwardRaw = forwardRaw.TrimEnd('_').TrimEnd();
                    _lastPanelForwarded.TryGetValue(source, out string prevPanel);
                    if (forwardRaw.Length > 0 && forwardRaw != prevPanel)
                    {
                        _lastPanelForwarded[source] = forwardRaw;
                        if (popupBlock)
                        {
                            try
                            {
                                object popup = Seams.PopUpControl_getCurrentPopUp?.Invoke(null, null);
                                string composite = Patches.PopupAnnouncePatch.ComposePanel(popup);
                                if (composite != null) ReviewLayer.NotePanel("Popup", composite);
                            }
                            catch { }
                        }
                        else
                        {
                            ReviewLayer.NotePanel(source, forwardRaw);
                        }
                    }
                }

                _lastContent.TryGetValue(source, out string prev);
                if (cleaned == prev) continue;

                // Locality furniture noted while a dialogue is up: hold it
                // (latest wins), release on dialogue exit. The diff record is
                // deliberately NOT updated here — release runs the value
                // through the normal spoken path.
                if (_inSceneFamily && System.Array.IndexOf(FurnitureSources, source) >= 0)
                {
                    _heldFurniture[source] = cleaned;
                    continue;
                }

                _lastContent[source] = cleaned;

                // The one config (owner ruling 2026-08-18): tooltips and
                // UI-nav-initiated populations auto-read their full body only
                // while Panel.AutoReadBody is on; off speaks the identity line
                // and leaves the body at its known address in the buffer.
                // Event surfaces (SecondaryDesc residue, SceneDesc, popups)
                // always speak in full.
                string toSpeak = cleaned;
                if (!PanelPolicy.AutoReadBody && (source == "Tooltip" || source == "SheetDesc"))
                {
                    PanelPolicy.EnsureTags();
                    string identity = Composer.IdentityLine(Composer.SectionPanel(source, forwardRaw));
                    if (!string.IsNullOrWhiteSpace(identity)) toSpeak = Composer.EnsurePeriod(identity);
                }

                if (kv.Value.Interrupt)
                    Scaffold.SpeechService.Say(toSpeak, source);
                else
                    Scaffold.SpeechService.SayQueued(toSpeak, source);

                // Dialogue node change (owner ruling 2026-08-17): picking a
                // choice mounts a new node INSIDE the same scene state — no
                // state change, so the entry-time announcement never re-fires
                // and the new choices went unspoken. The prose change IS the
                // node event; re-announce the choice row queued behind it
                // (the queue's dedup absorbs the state-entry overlap). The
                // list earmarks the focused row (", selected"), so the focus
                // event is absorbed into it — a node mount speaks exactly two
                // utterances, prose then the earmarked list, and the separate
                // focus line can never interrupt the prose (owner ruling
                // 2026-08-17, hold-and-flush doctrine).
                if (source == "SceneDesc")
                {
                    try
                    {
                        object state = CurrentStateObject();
                        if (state != null && Seams.SceneBaseStateType != null
                            && Seams.SceneBaseStateType.IsInstanceOfType(state))
                        {
                            object choices = GameStateTracker.AnnounceNumericButtons(state);
                            if (choices != null) AbsorbFocus(choices);
                        }
                    }
                    catch { }
                }
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

        /// <summary>Combat log SHORTS batch within the frame (the choke's two
        /// strings are (short, verbose) — survey §4c.12; the verbose ledgers
        /// stay in the game's own log, there is no mod verbose tier). Gated on
        /// the game's own current state — a live read, not a mod-side mode
        /// flag. In an active encounter the frame's shorts and pending barks
        /// hand to the CP2 composer (object-first attribution, lettered
        /// identifiers, merge grammar); the composer consumes matched barks in
        /// place and DrainBarks speaks the remainder. Fallback: plain
        /// coalesced speech, exactly the old shape.</summary>
        private static void DrainCombatLog()
        {
            // Bark-only frames narrate too (review find 2: Phalanx and other
            // log-less barks would otherwise skip attribution entirely) — but
            // log lines still drop outside combat states, and barks outside
            // combat stay untouched for the state-agnostic DrainBarks.
            var lines = new System.Collections.Generic.List<string>(_pendingCombatLog);
            _pendingCombatLog.Clear();

            object state = CurrentStateObject();
            string stateName = state?.GetType().Name ?? "";
            if (!stateName.Contains("Combat")) return;
            if (lines.Count == 0 && _pendingBarks.Count == 0) return;

            if (CombatSpine.NarrateCombatFrame(lines, _pendingBarks)) return;
            if (lines.Count == 0) return;

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
        /// state on arrival is always voiced) but QUEUES — list inits write
        /// their current object during screen build, and the arrival line must
        /// trail the entry read, not cut it (owner policy 2026-08-17). Repeats
        /// stay silent; real selection changes interrupt.</summary>
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
            if (_spokenListSelections.Add(list))
                Scaffold.SpeechService.SayQueued($"Selected: {name}.", "Nav");
            else Scaffold.SpeechService.Say($"Selected: {name}.", "Nav");
        }

        /// <summary>Speak filter changes once, at the settled index — "Filter:
        /// Weapon, 2 of 8." The name transcodes from the game's own icon path
        /// (the filters render no text); the trailing count follows the
        /// positional-counts rule. A spoken change clears the hover record so
        /// the different item now under the unmoved cursor re-speaks, queued
        /// behind this line.</summary>
        private static void DrainFilterChange()
        {
            object fc = _pendingFilterControl;
            if (fc == null) return;
            _pendingFilterControl = null;

            if (Seams.FilterControl_getFilterIndex == null || Seams.FilterControl_getIconPaths == null) return;
            int index = (int)Seams.FilterControl_getFilterIndex.Invoke(fc, null);
            if (ReferenceEquals(fc, _lastFilterControl) && index == _lastFilterIndex) return;
            _lastFilterControl = fc;
            _lastFilterIndex = index;

            var paths = Seams.FilterControl_getIconPaths.Invoke(fc, null) as System.Collections.IList;
            if (paths == null || index < 0 || index >= paths.Count) return;
            string name = FilterNameFromIconPath(paths[index] as string);
            if (name == null) return;

            _invHoverSegment = null;
            _invHoverRow = _invHoverCol = -1;
            _filterSpokeThisFrame = true;
            Scaffold.SpeechService.Say($"Filter: {name}, {index + 1} of {paths.Count}.", "Nav");
        }

        /// <summary>The game's own name for a filter, from its icon path:
        /// "Images/GUIIcons/InventoryUI/FilterWeapon" → "Weapon".</summary>
        private static string FilterNameFromIconPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int slash = path.LastIndexOf('/');
            string stem = slash >= 0 ? path.Substring(slash + 1) : path;
            if (stem.StartsWith("Filter")) stem = stem.Substring("Filter".Length);
            return string.IsNullOrWhiteSpace(stem) ? null : stem;
        }

        /// <summary>Speak the item under the hovered inventory cell on change
        /// — the hovered cell is the surface's focus truth (mouse and keyboard
        /// snaps both land here). Arrival observations queue per the entry
        /// discipline; stamped player-nav frames interrupt.</summary>
        private static void DrainInventoryHover()
        {
            object segment = _pendingInvHoverSegment;
            if (segment == null) return;
            int row = _pendingInvHoverRow, col = _pendingInvHoverCol;
            _pendingInvHoverSegment = null;
            _pendingInvHoverRow = _pendingInvHoverCol = -1;

            if (ReferenceEquals(segment, _invHoverSegment)
                && row == _invHoverRow && col == _invHoverCol) return;
            bool segmentChanged = !ReferenceEquals(segment, _invHoverSegment);
            _invHoverSegment = segment;
            _invHoverRow = row;
            _invHoverCol = col;

            string text = ComposeInventoryCellAt(segment, row, col);
            if (text == null) return;
            // Panel identity (owner ruling 2026-08-19): entering a DIFFERENT
            // grid names it — two grids on one sheet otherwise share a voice
            // ("Workstation. Empty." vs a bare "Empty.").
            if (segmentChanged)
            {
                string zone = HoverZoneLabelFor(segment);
                if (zone != null) text = $"{zone}. {text}";
            }
            bool arrival = _spokenCanvases.Add(segment);
            if (_filterSpokeThisFrame || (arrival && _playerNavFrame != Time.frameCount))
                Scaffold.SpeechService.SayQueued(text, "Nav");
            else Scaffold.SpeechService.Say(text, "Nav");
        }

        /// <summary>The hovered segment's panel label, resolved by identity
        /// against the current sheet's own grid fields (crafting and camp —
        /// the sheets whose twin grids the owner flagged as voice-identical).
        /// Null anywhere else.</summary>
        private static string HoverZoneLabelFor(object segment)
        {
            try
            {
                object sheet = _selControl;
                if (sheet == null || Seams.UIInventorySheetBaseType == null
                    || !Seams.UIInventorySheetBaseType.IsInstanceOfType(sheet)) return null;
                if (Seams.UIInventorySheetCraftingType != null
                    && Seams.UIInventorySheetCraftingType.IsInstanceOfType(sheet))
                {
                    if (ReferenceEquals(segment, Seams.InvSheet_secondaryInventoryGrid?.GetValue(sheet)))
                        return "Workstation";
                    if (ReferenceEquals(segment, Seams.InvSheet_mainInventoryGrid?.GetValue(sheet)))
                        return "Party Inventory";
                }
                else if (Seams.UIInventorySheetCampingFoodType != null
                    && Seams.UIInventorySheetCampingFoodType.IsInstanceOfType(sheet))
                {
                    if (ReferenceEquals(segment, Seams.InvSheet_secondaryInventoryGrid?.GetValue(sheet)))
                        return "Tonight's Meal";
                    if (ReferenceEquals(segment, Seams.InvSheet_mainInventoryGrid?.GetValue(sheet)))
                        return "Party's Food";
                }
            }
            catch { }
            return null;
        }

        /// <summary>Speak which arrow column the attribute editor's row cursor
        /// flipped onto — "Plus." / "Minus." ONLY, never the row body (owner
        /// ruling 2026-08-17) — read from the game's own
        /// controllerScrollToPlusButton flag at drain time (entry1; both
        /// entries flip together by construction).</summary>
        private static void DrainEditorFlip()
        {
            object sheet = _pendingEditorFlip;
            if (sheet == null) return;
            _pendingEditorFlip = null;

            if (Seams.CharacterSheet_entry1 == null || Seams.EditorEntry_scrollToPlusButton == null) return;
            object entry = Seams.CharacterSheet_entry1.GetValue(sheet);
            if (entry == null || Seams.EditorSheetEntryType == null
                || !Seams.EditorSheetEntryType.IsInstanceOfType(entry)) return;
            bool plus = (bool)Seams.EditorEntry_scrollToPlusButton.GetValue(entry);
            Scaffold.SpeechService.Say(plus ? "Plus." : "Minus.", "Nav");
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
            string line = isButtonRow ? $"Buttons: {text}." : text;
            // A popup announced this frame owns the interrupt — its own zone
            // line queues behind the body instead of cutting it off. A canvas
            // never heard this state is an arrival observation and queues too —
            // unless the player's own direction press produced it (the stamp):
            // player pane crossings always interrupt.
            bool arrival = _spokenCanvases.Add(canvas);
            if (_popupSpokeThisFrame || (arrival && _playerNavFrame != Time.frameCount))
                Scaffold.SpeechService.SayQueued(line, "Nav");
            else Scaffold.SpeechService.Say(line, "Nav");

            // Supersede the frame's selection note and align the dedup records
            // so the next real move on this canvas diffs correctly.
            _pendingSelection = null;
            _selControl = canvas;
            _selIndex = index;
            _selElement = FocusedElementOf(canvas, index);
            ReviewLayer.OnFocusChanged();
        }

        /// <summary>Absorb a focus event whose truth was just spoken inside a
        /// composed utterance (the earmarked choice list): align the selection
        /// dedup records to the canvas's settled focus and retire any pending
        /// note targeting the SAME canvas, so no separate focus line races the
        /// prose. A focus write landing frames later (the scene reveal's mouse
        /// snap re-writes the same value when the prose finishes revealing)
        /// hits the aligned records and dedups silently; a genuine later move
        /// diffs and speaks normally. Pending notes for other canvases are
        /// left alone — that focus is information this utterance didn't carry.
        /// The index clamp mirrors the game's boundCurrentSelectedButtons over
        /// the scrollable elements — the value its own snap path settles on.</summary>
        internal static void AbsorbFocus(object canvas)
        {
            if (canvas == null || Seams.UICanvas_currentSelectedButton == null) return;
            int index;
            try { index = (int)Seams.UICanvas_currentSelectedButton.GetValue(canvas); }
            catch { return; }
            try
            {
                var elements = Seams.UICanvas_getScrollableElements?.Invoke(canvas, null)
                    as System.Collections.IList;
                int count = elements != null ? elements.Count : 0;
                if (count > 0 && index >= count) index = count - 1;
            }
            catch { }
            if (index < 0) index = 0;

            _selControl = canvas;
            _selIndex = index;
            _selElement = FocusedElementOf(canvas, index);
            _spokenCanvases.Add(canvas); // the list utterance carried this
                                         // canvas's arrival — its next focus
                                         // event is a user move
            if (ReferenceEquals(_pendingSelection, canvas))
            {
                _pendingSelection = null;
                _pendingZoneLabel = null;
            }
            if (ReferenceEquals(_pendingCanvasSwitch, canvas))
                _pendingCanvasSwitch = null;
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
            object element = FocusedElementOf(control, index);

            if (ReferenceEquals(control, _selControl) && index == _selIndex)
            {
                // Same-(control,index) writes that land on a DIFFERENT element:
                // feat-tree laterals (a column/tree cursor moves, then index is
                // rewritten 0 — UIFeatTree.cs:447-457) and inventory-segment
                // column moves (the column cursor changes which cell each row
                // index means). The escape is scoped to surfaces whose element
                // objects are verified stable — a blanket element-identity key
                // could re-speak per frame on surfaces that rebuild their
                // element lists.
                bool elementMoved = element != null
                    && !ReferenceEquals(element, _selElement)
                    && ((Seams.FeatNodeType != null && Seams.FeatNodeType.IsInstanceOfType(element))
                        || (Seams.InventorySegmentType != null && Seams.InventorySegmentType.IsInstanceOfType(control))
                        // Inventory sheets: a column move changes which cell
                        // the sheet's row index means; cells are stable
                        // UIGridButton objects created once at ctor
                        // (UIGridBase.cs:59-78), so identity diffing is safe.
                        || (Seams.UIInventorySheetBaseType != null && Seams.UIInventorySheetBaseType.IsInstanceOfType(control)));
                if (!elementMoved) return;
            }
            // The hover join owns inventory-sheet cell speech: a W/S or A/D
            // snap produces BOTH a funnel note and a hover note in the same
            // frame — the hover names the cell the cursor actually landed on,
            // the funnel only its own belief. Align records, yield the voice.
            // EXCEPT on a zone crossing: the label must ride the line (a
            // yielded label would leak onto an unrelated later utterance —
            // the F2 class), and the crossing's snap makes the hover note
            // describe the same landing cell — absorb it instead.
            if (_pendingInvHoverSegment != null && Seams.UIInventorySheetBaseType != null
                && Seams.UIInventorySheetBaseType.IsInstanceOfType(control))
            {
                if (_pendingZoneLabel == null)
                {
                    _selControl = control;
                    _selIndex = index;
                    _selElement = element;
                    return;
                }
                _invHoverSegment = _pendingInvHoverSegment;
                _invHoverRow = _pendingInvHoverRow;
                _invHoverCol = _pendingInvHoverCol;
                _pendingInvHoverSegment = null;
                _pendingInvHoverRow = _pendingInvHoverCol = -1;
            }

            _selControl = control;
            _selIndex = index;
            _selElement = element;
            ReviewLayer.OnFocusChanged(); // review cursors reset with focus
            if (index < 0) return;

            // Consume the zone label before the null check — a failed
            // composition must never leave it to prefix an unrelated later
            // line (adversarial review F2; a lost label beats a leaked one).
            string zoneLabel = _pendingZoneLabel;
            _pendingZoneLabel = null;
            string text = ComposeSelection(control, index);
            if (text == null) return; // non-conforming control — graceful silence
            if (zoneLabel != null) text = $"{zoneLabel}. {text}";
            // A popup announced this frame owns the interrupt (its ctor's own
            // index-0 button write lands in the same drain) — the focus line
            // queues behind the body instead of cutting it to nothing. A canvas
            // never heard this state is an arrival observation (engine init
            // writes focus after content) and queues behind the entry read —
            // unless the player's own direction press produced it (the stamp):
            // player-driven moves always own the interrupt.
            bool arrival = _spokenCanvases.Add(control);
            if (_popupSpokeThisFrame || (arrival && _playerNavFrame != Time.frameCount))
                Scaffold.SpeechService.SayQueued(text, "Nav");
            else Scaffold.SpeechService.Say(text, "Nav");
        }

        /// <summary>The character-inventory funnel walks the SHEET: its
        /// scrollable elements delegate to the controller focus surface but
        /// its increment writes the sheet's own index
        /// (UIInventorySheetBase.cs:586-604) — so selection notes carry the
        /// sheet while the cells live on the surface. Resolve the surface the
        /// game itself would walk: currentControllerSurface, falling back to
        /// mainInventoryGrid exactly as getControllerFocusSurface does. Null
        /// when the control is not an inventory sheet.</summary>
        internal static object InventorySurfaceOf(object control)
        {
            try
            {
                if (control == null || Seams.UIInventorySheetBaseType == null
                    || !Seams.UIInventorySheetBaseType.IsInstanceOfType(control)
                    || Seams.InvSheet_currentControllerSurface == null) return null;
                object surface = Seams.InvSheet_currentControllerSurface.GetValue(control);
                if (surface == null && Seams.InvSheet_mainInventoryGrid != null)
                    surface = Seams.InvSheet_mainInventoryGrid.GetValue(control);
                return surface;
            }
            catch { return null; }
        }

        /// <summary>The element sitting at a control's index, resolved through
        /// the game's own scrollable-elements read. Null when unresolvable.</summary>
        private static object FocusedElementOf(object control, int index)
        {
            try
            {
                if (index < 0 || control == null || Seams.UICanvas_getScrollableElements == null) return null;
                var elements = Seams.UICanvas_getScrollableElements.Invoke(control, null)
                    as System.Collections.IList;
                if (elements == null || index >= elements.Count) return null;
                return elements[index];
            }
            catch { return null; }
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

            // Loot-popup item cells (2026-08-17): image cells in inventory
            // order — name from the popup's own item list, read-only.
            if (Seams.UIGridInventoryType != null && Seams.UIGridInventoryType.IsInstanceOfType(control))
                return ComposeLootCell(control, index);

            // Character-inventory segment cells (2026-08-17): the selection
            // index is a ROW at the segment's current column — map onto the
            // inventory's own filtered list.
            if (Seams.InventorySegmentType != null && Seams.InventorySegmentType.IsInstanceOfType(control))
                return ComposeInventoryCell(control, index);

            // Worn zone (2026-08-17): while active the sheet's rows are the
            // worn grid's two — name the slot and its item from the same
            // Character getters the renderer paints from.
            if (Patches.WornZonePatch.IsActiveFor(control))
                return ComposeWornCell(index);

            // Character-inventory SHEET (owner ride 2026-08-17 — cells were
            // silent): the native funnel writes its index on the SHEET, never
            // a segment, so the branch above never matched a funnel note.
            // Resolve the sheet's focus surface and compose its cell at the
            // sheet's row.
            object invSurface = InventorySurfaceOf(control);
            if (invSurface != null && Seams.InventorySegmentType != null
                && Seams.InventorySegmentType.IsInstanceOfType(invSurface))
                return ComposeInventoryCell(invSurface, index);

            int count = -1;
            string text = null;
            bool isCurrentListRow = false;
            bool isAvailableRow = false;

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
                        // Green = the game's own "available" convention (its
                        // tutorial popup names it: recipe craftable from
                        // stock) — transcode, never strip (2026-08-19).
                        string green = GreenTag();
                        isAvailableRow = green != null && raw != null && raw.StartsWith(green);
                        if (!string.IsNullOrWhiteSpace(raw) && raw != " ")
                        {
                            string cleaned = Patches.TextCleaner.CleanText(raw);
                            if (!string.IsNullOrWhiteSpace(cleaned))
                                text = (cleaned == "..." || cleaned == "…") ? "dot dot dot"
                                    // Load/save pad rows (§6.2 transcode): the
                                    // game's own empty-slot tag, spoken plainly.
                                    : cleaned == "--empty--" ? "Empty slot"
                                    : cleaned;
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
            // settings sliders, the CAMP ACTIVITY column): the scrollable
            // elements are each row's chosen minus/plus arrow, so the
            // buttons-list path never matches. Map the arrow back to its
            // owning row and speak the slider composition (closes the
            // B2-class silence on vertical nav over slider rows). When the
            // control is an inventory SHEET delegating to a slider surface
            // (camp activities), the rows live on the SURFACE, not the sheet
            // — resolve against it (camp survey fix 2026-08-19: W/S over
            // party members spoke nothing).
            if (text == null && Seams.UICanvas_getScrollableElements != null)
            {
                try
                {
                    var elements = Seams.UICanvas_getScrollableElements.Invoke(control, null)
                        as System.Collections.Generic.List<UIElement>;
                    if (elements != null && index >= 0 && index < elements.Count)
                    {
                        object rowOwner = control;
                        if (invSurface != null && Seams.UITextSliderControlType != null
                            && Seams.UITextSliderControlType.IsInstanceOfType(invSurface))
                            rowOwner = invSurface;
                        object row = Patches.SliderArrowPatch.RowForScrollableElement(rowOwner, elements[index]);
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

            // Image-only elements (feat-tree nodes): the node renders name-in-icon
            // pixels, pips, and a padlock — all transcoded from the feat's own
            // data (rank, legality, prerequisite; phrasing rulings 2026-08-16).
            // The count trails as nodes-in-current-column (no column ordinals —
            // owner ruling: geometry doesn't map cleanly to structure; the
            // prerequisite edge carries the orientation instead).
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
                            text = ComposeFeatNode(control, feat);
                        }
                    }
                }
                catch { }
            }

            // An empty feat column would otherwise be a silent lateral landing.
            if (text == null && Seams.UIFeatTreeType != null
                && Seams.UIFeatTreeType.IsInstanceOfType(control))
                text = "Empty column.";

            // Generic element-text fallback (2026-08-17): sheet canvases hand
            // the join the whole sheet, whose scrollable elements are text
            // rows that already render the game's own "Name: Value" composite
            // (SkaldDataList.getListName) — read the focused row's rendered
            // text directly. Unlocks the in-game character/attribute/grimoire
            // sheets; header rows transcode to a spoken heading.
            if (text == null && Seams.UICanvas_getScrollableElements != null
                && Seams.UITextBlock_content != null)
            {
                try
                {
                    var elements = Seams.UICanvas_getScrollableElements.Invoke(control, null)
                        as System.Collections.Generic.List<UIElement>;
                    if (elements != null && index >= 0 && index < elements.Count)
                    {
                        string raw = Seams.UITextBlock_content.GetValue(elements[index]) as string;
                        if (!string.IsNullOrWhiteSpace(raw) && raw != " ")
                        {
                            count = elements.Count;
                            string header = HeaderTag();
                            bool isHeader = header != null && raw.StartsWith(header);
                            string cleaned = Patches.TextCleaner.CleanText(raw);
                            if (!string.IsNullOrWhiteSpace(cleaned))
                                text = isHeader ? $"Heading: {cleaned}" : cleaned;
                        }
                    }
                }
                catch { /* element is not a text block — stay silent */ }
            }

            if (text == null) return null;

            // Numeric-class rows by registry-audited type identity (WP8) — a
            // rename shows up in the boot report instead of silently demoting
            // the row to a browse counter.
            Type controlType = control.GetType();
            bool numericClass = controlType == Seams.SheetButtonControlType
                || controlType == Seams.NumericButtonControlType
                || controlType == Seams.MenuButtonControlType;

            if (numericClass) return $"{index + 1}: {GameStateTracker.TranscodeQuickLabel(text, index, CurrentStateObject())}";
            if (isCurrentListRow) text = $"{text}, selected";
            if (isAvailableRow) text = $"{text}, available";
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

                // Popup spell selector (2026-08-17): its grid is the same
                // UIAbilitySelectorGrid family but its data lives on the popup
                // — read the popup's own legal-spells list, index-aligned by
                // construction (PopUpSpellSelector.handle rebuilds the grid
                // from that exact list every frame). The game echoes the
                // hovered name into the tertiary line — seed that instead of
                // SecondaryDesc.
                bool fromSpellPopup = false;
                string qualifier = null;
                if (raw == null && Seams.PopUpSpellSelectorType != null
                    && Seams.PopUpSpellSelector_getLegalSpells != null
                    && Seams.PopUpControl_getCurrentPopUp != null
                    && Seams.SkaldBaseObject_getName != null)
                {
                    object popup = Seams.PopUpControl_getCurrentPopUp.Invoke(null, null);
                    if (popup != null && Seams.PopUpSpellSelectorType.IsInstanceOfType(popup))
                    {
                        var spells = Seams.PopUpSpellSelector_getLegalSpells.Invoke(popup, null)
                            as System.Collections.IList;
                        if (spells != null && index >= 0 && index < spells.Count)
                        {
                            object spell = spells[index];
                            raw = Seams.SkaldBaseObject_getName.Invoke(spell, null) as string;
                            fromSpellPopup = true;
                            // Padlock / selected overlays, transcoded from the
                            // renderer's own conditions (createButtonDataList:
                            // locked = tier above the pick-tier; selected =
                            // id in the picked list). The tier IS the lock
                            // reason — the game's own rejection wording.
                            try
                            {
                                if (Seams.AbilitySpell_getTier != null
                                    && Seams.SpellSelector_tierOfSpellsToSelect != null)
                                {
                                    int tier = (int)Seams.AbilitySpell_getTier.Invoke(spell, null);
                                    int pickTier = (int)Seams.SpellSelector_tierOfSpellsToSelect.GetValue(popup);
                                    if (tier > pickTier)
                                        qualifier = $"tier {tier}, locked";
                                    else if (Seams.SpellSelector_spellsSelected != null
                                        && Seams.SkaldBaseObject_getId != null)
                                    {
                                        var picked = Seams.SpellSelector_spellsSelected.GetValue(popup)
                                            as System.Collections.IList;
                                        string id = Seams.SkaldBaseObject_getId.Invoke(spell, null) as string;
                                        if (picked != null && id != null && picked.Contains(id))
                                            qualifier = "chosen"; // owner wording 2026-08-17
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }

                string name = string.IsNullOrWhiteSpace(raw) ? null : Patches.TextCleaner.CleanText(raw);
                if (string.IsNullOrWhiteSpace(name)) return null;

                SeedContent(fromSpellPopup ? "PopupTertiary" : "SecondaryDesc", name);
                if (qualifier != null) name = $"{name}, {qualifier}";
                return count > 1 ? $"{name}, {index + 1} of {count}" : name;
            }
            catch { return null; }
        }

        /// <summary>Loot-popup item cell: name and amount from the popup's own
        /// inventory list at the cell's index (grid order = inventory order by
        /// construction), trailing count. The game's own hover echo lands in
        /// the tertiary line a frame later — seed it so the echo dedups.</summary>
        private static string ComposeLootCell(object grid, int index)
        {
            try
            {
                var items = Patches.PopupGridNavPatch.CurrentLootItemList();
                if (items == null || index < 0 || index >= items.Count) return null;
                string raw = Seams.Item_getNameAndAmount != null
                    ? Seams.Item_getNameAndAmount.Invoke(items[index], null) as string
                    : Seams.SkaldBaseObject_getName?.Invoke(items[index], null) as string;
                string name = string.IsNullOrWhiteSpace(raw) ? null : Patches.TextCleaner.CleanText(raw);
                if (name == null) return null;
                SeedContent("PopupTertiary", name);
                return items.Count > 1 ? $"{name}, {index + 1} of {items.Count}" : name;
            }
            catch { return null; }
        }

        /// <summary>Feat-node terse line (phrasing rulings 2026-08-16):
        /// "Cleave, rank 2 of 3" / "Whirlwind, rank 0 of 2, locked, requires
        /// Cleave", prefixed "Tree: X." when the landing crossed into a
        /// different root tree (a tree has no name of its own — its root
        /// feat's name is the game-sanctioned label). Level requirements and
        /// the full description stay in the review panel. The generic tail
        /// appends the in-column browse counter.</summary>
        private static string ComposeFeatNode(object control, object feat)
        {
            if (feat == null) return null;
            string name = Seams.Feat_getName.Invoke(feat, null) as string;
            if (string.IsNullOrWhiteSpace(name)) return null;

            var parts = new System.Collections.Generic.List<string>
                { Patches.TextCleaner.CleanText(name) };

            object tree = CurrentFeatTreeOf(control);

            try
            {
                if (Seams.Feat_getRank != null && Seams.Feat_getMaxRankLevel != null)
                {
                    int rank = (int)Seams.Feat_getRank.Invoke(feat, null);
                    int max = (int)Seams.Feat_getMaxRankLevel.Invoke(feat, null);
                    if (max > 0) parts.Add($"rank {rank} of {max}");
                }
            }
            catch { }

            try
            {
                if (Seams.Feat_isLegal != null && !(bool)Seams.Feat_isLegal.Invoke(feat, null))
                {
                    parts.Add("locked");
                    // Inline only the prerequisite-feat edge (the padlock's
                    // in-tree cause); the level gate stays panel-side.
                    if (Seams.Feat_legalPrereqFeat != null
                        && !(bool)Seams.Feat_legalPrereqFeat.GetValue(feat))
                    {
                        string parent = FeatParentName(tree, feat);
                        if (parent != null) parts.Add($"requires {parent}");
                    }
                }
            }
            catch { }

            string line = string.Join(", ", parts.ToArray());

            // Tree-crossing prefix. Mutating the record here is deliberate:
            // every crossing arrives through a real selection drain; re-compose
            // paths (review re-anchor) land on the same tree and add nothing.
            if (tree != null && !ReferenceEquals(tree, _lastFeatTree))
            {
                _lastFeatTree = tree;
                string root = FeatTreeRootName(tree);
                if (root != null) line = $"Tree: {root}. {line}";
            }
            return line;
        }

        /// <summary>The FeatTree the controller cursor currently sits in, read
        /// from the game's own collection index.</summary>
        private static object CurrentFeatTreeOf(object control)
        {
            try
            {
                if (Seams.UIFeatTree_treeCollection == null
                    || Seams.FeatTreeCollection_controllerScrollIndex == null
                    || Seams.UICanvas_getElements == null) return null;
                object collection = Seams.UIFeatTree_treeCollection.GetValue(control);
                if (collection == null) return null;
                int ti = (int)Seams.FeatTreeCollection_controllerScrollIndex.GetValue(collection);
                var trees = Seams.UICanvas_getElements.Invoke(collection, null)
                    as System.Collections.IList;
                if (trees == null || ti < 0 || ti >= trees.Count) return null;
                return trees[ti];
            }
            catch { return null; }
        }

        /// <summary>A tree's game-sanctioned label: its root feat's name (the
        /// root node is the first element added in the tree's ctor).</summary>
        private static string FeatTreeRootName(object tree)
        {
            try
            {
                var elements = Seams.UICanvas_getElements.Invoke(tree, null)
                    as System.Collections.IList;
                if (elements == null || elements.Count == 0) return null;
                object rootNode = elements[0];
                if (Seams.FeatNodeType == null || !Seams.FeatNodeType.IsInstanceOfType(rootNode)) return null;
                object feat = Seams.FeatNode_feat.GetValue(rootNode);
                string name = feat != null ? Seams.Feat_getName.Invoke(feat, null) as string : null;
                return string.IsNullOrWhiteSpace(name) ? null : Patches.TextCleaner.CleanText(name);
            }
            catch { return null; }
        }

        /// <summary>The prerequisite feat's display name, resolved through the
        /// tree's own id→node dictionary (prereq chains stay in-tree by
        /// construction — children append under their root).</summary>
        private static string FeatParentName(object tree, object feat)
        {
            try
            {
                if (tree == null || Seams.Feat_getPrerequisitFeat == null
                    || Seams.FeatTree_nodeDictionary == null) return null;
                string prereqId = Seams.Feat_getPrerequisitFeat.Invoke(feat, null) as string;
                if (string.IsNullOrEmpty(prereqId)) return null;
                var dict = Seams.FeatTree_nodeDictionary.GetValue(tree)
                    as System.Collections.IDictionary;
                if (dict == null || !dict.Contains(prereqId)) return null;
                object parentFeat = Seams.FeatNode_feat.GetValue(dict[prereqId]);
                string name = parentFeat != null ? Seams.Feat_getName.Invoke(parentFeat, null) as string : null;
                return string.IsNullOrWhiteSpace(name) ? null : Patches.TextCleaner.CleanText(name);
            }
            catch { return null; }
        }

        /// <summary>Inventory-segment cell: item index = page offset +
        /// row * gridWidth + column over the same type-filtered list the
        /// segment renders from (UIInventorySheetBase.cs:113-137). Speaks
        /// name-and-amount with the item's position among the segment's
        /// items; a cell past the list's end is honestly empty.</summary>
        private static string ComposeInventoryCell(object segment, int row)
        {
            try
            {
                if (Seams.InvSegment_column == null) return null;
                return ComposeInventoryCellAt(segment, row,
                    (int)Seams.InvSegment_column.GetValue(segment));
            }
            catch { return null; }
        }

        // The cell composes were failing to TOTAL silence with the reason
        // swallowed by their own catch (owner ride 2026-08-17: vacant cells
        // spoke nothing, not even "Empty."). Every distinct failure reason
        // logs once per session — a receipt, not noise.
        private static readonly System.Collections.Generic.HashSet<string> _invCellFailReasons
            = new System.Collections.Generic.HashSet<string>();
        private static void LogInvCellFailOnce(string reason)
        {
            if (_invCellFailReasons.Add(reason))
                Plugin.Logger?.LogWarning($"[InvCell] compose failed: {reason}");
        }

        /// <summary>Worn-slot labels in the renderer's own setButtons order
        /// (UIInventorySheetBase.cs:232-246), named from the game's own icon
        /// files (WornIconMelee … WornIconNecklace — the slots render no
        /// text): row 0 then row 1, left to right.</summary>
        private static readonly string[] _wornSlotLabels =
        {
            "Melee", "Ranged", "Armor", "Shield", "Ammo", "Ring",
            "Head", "Clothing", "Hands", "Feet", "Off hand", "Necklace"
        };

        /// <summary>Worn cell at (funnel row, zone column): slot label plus
        /// the item from the same Character getter the renderer paints the
        /// slot from — "Melee: Longsword" / "Head: empty". Row and column
        /// clamp exactly as the game's bound-on-read does.</summary>
        private static string ComposeWornCell(int row)
        {
            try
            {
                var getters = Seams.Character_wornGetters;
                object character = Patches.WornZonePatch.WornCharacter;
                if (getters == null || character == null)
                { LogInvCellFailOnce("worn: character or getters unavailable"); return null; }

                int width = _wornSlotLabels.Length / 2;
                int col = Patches.WornZonePatch.Column;
                if (row < 0) row = 0; else if (row > 1) row = 1;
                if (col < 0) col = 0; else if (col >= width) col = width - 1;
                int slot = row * width + col;
                if (slot >= getters.Length || getters[slot] == null) return null;

                string label = _wornSlotLabels[slot];
                object item = getters[slot].Invoke(character, null);
                if (item == null) return $"{label}: empty";
                string raw = Seams.Item_getNameAndAmount != null
                    ? Seams.Item_getNameAndAmount.Invoke(item, null) as string
                    : Seams.SkaldBaseObject_getName?.Invoke(item, null) as string;
                string name = string.IsNullOrWhiteSpace(raw) ? null : Patches.TextCleaner.CleanText(raw);
                return name == null ? $"{label}: empty" : $"{label}: {name}";
            }
            catch (System.Exception ex)
            {
                LogInvCellFailOnce($"worn: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Same map with an explicit column — the hover join names
        /// the cell the cursor actually sits on, independent of the funnel's
        /// column field.</summary>
        internal static string ComposeInventoryCellAt(object segment, int row, int column)
        {
            try
            {
                if (Seams.InvSegment_inventory == null || Seams.Inventory_getListByType == null
                    || Seams.InvSegment_gridWidth == null
                    || Seams.InvSegment_offsetIndex == null)
                { LogInvCellFailOnce("seam handles missing"); return null; }

                object inventory = Seams.InvSegment_inventory.GetValue(segment);
                // itemTypes null is NOT a failure: the default "All" filter is
                // literally a null type list (UIInventorySheetBase.cs:348) and
                // getListByType defines null as no-filter (Inventory.cs:841).
                // Treating it as one silenced every cell under the default
                // filter (the whole 2026-08-17 hunt).
                object itemTypes = Seams.InvSegment_itemTypes?.GetValue(segment);
                if (inventory == null) { LogInvCellFailOnce("segment.inventory null"); return null; }

                var items = Seams.Inventory_getListByType.Invoke(inventory, new[] { itemTypes, (object)false })
                    as System.Collections.IList;
                if (items == null) { LogInvCellFailOnce("getListByType returned null/non-list"); return null; }

                int width = (int)Seams.InvSegment_gridWidth.GetValue(segment);
                int offset = (int)Seams.InvSegment_offsetIndex.GetValue(segment);
                int itemIndex = offset + row * width + column;

                if (itemIndex < 0 || itemIndex >= items.Count) return "Empty.";

                string raw = Seams.Item_getNameAndAmount != null
                    ? Seams.Item_getNameAndAmount.Invoke(items[itemIndex], null) as string
                    : Seams.SkaldBaseObject_getName?.Invoke(items[itemIndex], null) as string;
                string name = string.IsNullOrWhiteSpace(raw) ? null : Patches.TextCleaner.CleanText(raw);
                if (name == null) return "Empty.";
                return items.Count > 1 ? $"{name}, {itemIndex + 1} of {items.Count}" : name;
            }
            catch (System.Exception ex)
            {
                LogInvCellFailOnce($"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>The game's header markup tag (lazy, post-ready — the
        /// element-text fallback transcodes header rows to a spoken heading
        /// instead of stripping the markup flat).</summary>
        private static string _headerTag;
        private static string HeaderTag()
        {
            if (_headerTag != null) return _headerTag.Length == 0 ? null : _headerTag;
            _headerTag = Seams.TagValue(Seams.C64_HeaderTag) ?? "";
            return _headerTag.Length == 0 ? null : _headerTag;
        }

        /// <summary>The game's own current-row marker, read once from
        /// C64Color.YELLOW_TAG via the Seams handle (colors load with game
        /// data; composition only runs post-ready, so the lazy value read is
        /// safe here — never at Awake).</summary>
        private static string _greenTag;
        private static string GreenTag()
        {
            if (_greenTag != null) return _greenTag.Length == 0 ? null : _greenTag;
            _greenTag = Seams.TagValue(Seams.C64_GreenLightTag) ?? "";
            return _greenTag.Length == 0 ? null : _greenTag;
        }

        private static string YellowTag()
        {
            if (_yellowTag != null) return _yellowTag.Length == 0 ? null : _yellowTag;
            _yellowTag = Seams.TagValue(Seams.C64_YellowTag) ?? "";
            if (_yellowTag.Length == 0)
                Plugin.Logger?.LogWarning("[Pump:sel] C64Color.YELLOW_TAG unavailable — selected-row state unvoiced");
            return _yellowTag.Length == 0 ? null : _yellowTag;
        }

        /// <summary>Character-creation point speech (rulings 2026-08-16).
        /// A successful plus/minus press speaks the row's new value and the
        /// pool in one line ("Strength 2. 3 attribute points remaining."); a
        /// rejected press speaks its edge, never silence. A feat buy/refund
        /// speaks the new rank ("Cleave, rank 2 of 3.") with the game's own
        /// "Ranks to Distribute" line queued behind it via the FeatPoints
        /// content source. Pool changes without a press (screen entry, resets)
        /// announce queued.</summary>
        private static void DrainPoints()
        {
            // Feat rank line first — its remaining-pool companion arrives
            // through the content drain this same frame and queues behind.
            object feat = _pendingFeatRank;
            if (feat != null)
            {
                _pendingFeatRank = null;
                try
                {
                    string name = Seams.Feat_getName?.Invoke(feat, null) as string;
                    name = string.IsNullOrWhiteSpace(name) ? null : Patches.TextCleaner.CleanText(name);
                    if (name != null && Seams.Feat_getRank != null && Seams.Feat_getMaxRankLevel != null)
                    {
                        int rank = (int)Seams.Feat_getRank.Invoke(feat, null);
                        int max = (int)Seams.Feat_getMaxRankLevel.Invoke(feat, null);
                        Scaffold.SpeechService.Say($"{name}, rank {rank} of {max}.", "Points");
                    }
                }
                catch { }
            }

            // Cascade refunds (owner phrasing 2026-08-17): state the diff,
            // remaining points trail via the FeatPoints line once the tally
            // settles. Queued — the triggering press's rank line owns the
            // interrupt.
            if (_pendingRefunds.Count > 0)
            {
                foreach (object refunded in _pendingRefunds)
                {
                    _refundTally.TryGetValue(refunded, out int n);
                    _refundTally[refunded] = n + 1;
                }
                _pendingRefunds.Clear();
                _refundQuietFrames = 0;
            }
            else if (RefundSettling && ++_refundQuietFrames >= 2)
            {
                int total = 0;
                var names = new System.Collections.Generic.List<string>();
                foreach (var kv in _refundTally)
                {
                    total += kv.Value;
                    try
                    {
                        string n = Seams.Feat_getName?.Invoke(kv.Key, null) as string;
                        n = string.IsNullOrWhiteSpace(n) ? null : Patches.TextCleaner.CleanText(n);
                        if (n != null) names.Add(n);
                    }
                    catch { }
                }
                _refundTally.Clear();
                if (names.Count > 0)
                {
                    string joined = string.Join(", ", names.ToArray());
                    Scaffold.SpeechService.SayQueued(total == 1
                        ? $"Point removed from {joined}."
                        : $"{total} points removed from {joined}.", "Points");
                }
            }

            if (_pendingPress.HasValue)
            {
                var press = _pendingPress.Value;
                _pendingPress = null;
                SpeakPointPress(press);
            }

            // Pool values not consumed by a press line: entry/reset announcements.
            if (_pendingAttrPool.HasValue)
            {
                int v = _pendingAttrPool.Value;
                _pendingAttrPool = null;
                if (v != _lastAttrPool)
                {
                    _lastAttrPool = v;
                    Scaffold.SpeechService.SayQueued(PoolPhrase(v, "attribute"), "Points");
                }
            }
            if (_pendingSkillPool.HasValue)
            {
                int v = _pendingSkillPool.Value;
                _pendingSkillPool = null;
                if (v != _lastSkillPool)
                {
                    _lastSkillPool = v;
                    Scaffold.SpeechService.SayQueued(PoolPhrase(v, "skill"), "Points");
                }
            }
        }

        private static string PoolPhrase(int v, string kind)
            => $"{v} {kind} point{(v == 1 ? "" : "s")} remaining.";

        private static void SpeakPointPress(PointPress press)
        {
            string kind = press.IsAttribute ? "attribute" : "skill";
            int? pending = press.IsAttribute ? _pendingAttrPool : _pendingSkillPool;
            int last = press.IsAttribute ? _lastAttrPool : _lastSkillPool;

            string name = null;
            try
            {
                string raw = Seams.SkaldBaseObject_getName?.Invoke(press.Row, null) as string;
                if (!string.IsNullOrWhiteSpace(raw)) name = Patches.TextCleaner.CleanText(raw);
            }
            catch { }

            bool success = pending.HasValue && pending.Value != last;
            if (success)
            {
                int pool = pending.Value;
                if (press.IsAttribute) { _pendingAttrPool = null; _lastAttrPool = pool; }
                else { _pendingSkillPool = null; _lastSkillPool = pool; }

                // The row's new value: the game renders the attribute rank
                // itself, so the rank read IS the displayed number (the fresh
                // rendered row is impractical to re-identify at the drain).
                string value = PressedRowValue(press.Row);
                string head = name == null ? null
                    : value != null ? $"{name} {value}." : $"{name}.";
                string tail = PoolPhrase(pool, kind);
                Scaffold.SpeechService.Say(head == null ? tail : $"{head} {tail}", "Points");
                return;
            }

            // Rejected press — mirror the game's own guard order (pool first,
            // then rank cap), never silence.
            int poolNow = pending ?? last;
            if (press.IsPlus && poolNow == 0)
                Scaffold.SpeechService.Say($"No {kind} points remaining.", "Points");
            else if (press.IsPlus)
                Scaffold.SpeechService.Say($"{(name ?? "Value")} is at maximum.", "Points");
            else
                Scaffold.SpeechService.Say($"{(name ?? "Value")} is at minimum.", "Points");
        }

        /// <summary>The pressed row's post-mutation value, read from the
        /// character under construction via the game's own accessors.</summary>
        private static string PressedRowValue(object row)
        {
            try
            {
                if (Seams.CharacterBuilderBaseStateType == null
                    || Seams.CharacterBuilderBase_getCharacter == null
                    || Seams.Character_getAttributeRank == null
                    || Seams.SkaldBaseObject_getId == null) return null;
                object state = CurrentStateObject();
                if (state == null || !Seams.CharacterBuilderBaseStateType.IsInstanceOfType(state)) return null;
                object character = Seams.CharacterBuilderBase_getCharacter.Invoke(state, null);
                string id = Seams.SkaldBaseObject_getId.Invoke(row, null) as string;
                if (character == null || id == null) return null;
                int rank = (int)Seams.Character_getAttributeRank.Invoke(character, new object[] { id });
                return rank.ToString();
            }
            catch { return null; }
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
            Scaffold.Log.ClockNow();        // wall time on every transition (L3)
            Scaffold.Log.HealthRollup();    // seam failures since last rollup (L5)

            // Locality hold window (owner ruling 2026-08-17). Entering the
            // scene family also RECLAIMS furniture already sitting in the
            // speech queue — an auto-triggered dialogue mounts one frame
            // after its region state, so the region header is queued before
            // the transition is visible. Leaving the family releases the
            // held lines queued, and sets their diff records first so the
            // returning state's own same-value repaint dedups.
            bool nowScene = IsSceneFamily(state);
            if (nowScene && !_inSceneFamily)
            {
                foreach (string source in FurnitureSources)
                {
                    var reclaimed = Scaffold.SpeechService.ExtractSource(source);
                    if (reclaimed.Count > 0)
                        _heldFurniture[source] = reclaimed[reclaimed.Count - 1];
                }
            }
            else if (!nowScene && _inSceneFamily && _heldFurniture.Count > 0)
            {
                foreach (var kv in _heldFurniture)
                {
                    _lastContent[kv.Key] = kv.Value;
                    Scaffold.SpeechService.SayQueued(kv.Value, kv.Key);
                }
                _heldFurniture.Clear();
            }
            _inSceneFamily = nowScene;

            ReviewLayer.OnStateTransition();    // review never survives a state change
            DialogueCursor.OnStateTransition(); // text mode dies with its scene
            PanelPolicy.OnStateTransition();    // fact differ re-seeds: first strip
                                                // in a new state settles silently (B7)
            OverlandCursor.OnStateTransition(); // cursor hold drops; the POI list
                                                // suspends (combat force-closes it)
            CombatCursor.OnStateTransition();   // survives intra-combat churn, dies on leaving the family
            TableCursor.OnStateTransition();    // section redirect dies with its screen
            Patches.SheetGridZonePatch.OnStateTransition(); // nor a sheet-grid zone
            Patches.MouseGuardPatch.OnStateTransition();    // nor a snap latch
                                                            // (entry snaps re-latch)
            Patches.WornZonePatch.OnStateTransition();      // zone dies with its screen
            Patches.CampZonePatch.OnStateTransition();      // camp entry facts re-arm
            _pendingZoneLabel = null;
            // Point-pool records reset per state so re-entering an editor
            // screen re-announces its pools (the diff records otherwise
            // outlive the screen and dedup the entry lines to silence).
            _lastAttrPool = int.MinValue;
            _lastSkillPool = int.MinValue;
            _lastContent.Remove("FeatPoints");
            _pendingRefunds.Clear();   // a mid-settle exit must not carry a
            _refundTally.Clear();      // refund line into the next state
            _spokenCanvases.Clear();       // every canvas's next focus line is an
            _spokenListSelections.Clear(); // arrival observation in the new state
            _invHoverSegment = null;       // hover records die with the screen
            _invHoverRow = _invHoverCol = -1;
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

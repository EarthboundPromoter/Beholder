using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SkaldAccessibility
{
    /// <summary>
    /// CP3 (combat-spec §6): the combat cursor — the WP11 overland cursor
    /// retargeted per the survey §6 shopping list and the 2026-08-18 phrasing
    /// stop. The cursor IS the game's virtual mouse (same latch discipline);
    /// Z and X keep native meanings (Z = left click: path/attack/placement,
    /// X = right click: inspect tooltip). WASD character movement is never
    /// captured.
    ///
    /// Retarget list honored: anchor = the ACTING character (encounter
    /// current character; the current PC during placement) — offsets and
    /// in-sight both key on it; state gates = CombatBaseState +
    /// CombatPlacementState (the cursor survives intra-combat state churn and
    /// dies on leaving the family); two rings only — hostiles/friendlies
    /// (owner ruling; corpses excluded, the ladder still names them on their
    /// tiles); the course join does NOT transfer — the path fact reads the
    /// acting character's own hover-recomputed course.
    ///
    /// The unified landing readout (owner ruling): one grammar for manual
    /// arrows, ring steps, and K-list navigation — label, offset, path/cost
    /// fact — with a trailing ring counter when a scan/list positioned the
    /// cursor. The line is spoken ONE FRAME DEFERRED: the nudge moves the
    /// virtual mouse, the game's own update re-hovers and recomputes the
    /// course, and the drain then composes from settled truth (joins respect
    /// engine order).
    ///
    /// Placement (owner ruling): validity LEADS ("Valid, Open, 2 east.") with
    /// Valid/Invalid as the pair, position configurable; the perimeter
    /// crossing itself speaks "Out of bounds."; the entry census ships behind
    /// a config flag, default off.
    ///
    /// Cost forecasts on the landing line (owner grammar — the game's own
    /// pips mispaint these, survey §4b.17): in melee without evasion, any
    /// move is "moving disengages, ends turn"; a live PC's tile is "swap ends
    /// turn" (or "swap costs 1 move" with freeSwap).
    /// </summary>
    public static class CombatCursor
    {
        // ---- Config (bound from Plugin.Awake) ----
        private static ConfigEntry<bool> _cfgLight;
        private static ConfigEntry<bool> _cfgValidPrepend;
        private static ConfigEntry<bool> _cfgPlacementCensus;

        internal static void BindConfig(ConfigFile config)
        {
            _cfgLight = config.Bind("Combat", "LightOnLanding", true,
                "Append the tile's light level to combat cursor landings (owner ruling: independent of the game's Show Stealth Info setting).");
            _cfgValidPrepend = config.Bind("Combat", "PlacementQualifierLeads", true,
                "During deployment, speak Valid/Invalid before the tile label (false = after).");
            _cfgPlacementCensus = config.Bind("Combat", "PlacementCensus", false,
                "Speak the number of valid deployment tiles when the deploy screen opens.");
        }

        private enum Ring { Hostiles, Friendlies }
        private static readonly string[] RingNames = { "hostiles", "friendlies" };

        private struct Entry
        {
            public int X, Y;
            public string Label;
            public int Dist;
        }

        // ---- Cursor state ----
        private static bool _held;
        private static int _tileX, _tileY;
        private static object _encounterSeen;

        /// <summary>The combat latch owns the virtual mouse while held — the
        /// general mouse guard defers to it (HoldsMouse peer, survey §6 ⑥).</summary>
        internal static bool HoldsMouse => _held && InCombat();

        // ---- Deferred landing (speak from settled truth, one frame later).
        //      The pending slot stores its own coordinates so a fast second
        //      nudge flushes the first landing instead of dropping it
        //      (review find 6). ----
        private static bool _pendingSpeak;
        private static int _pendingFrame = -1;
        private static int _pendingX, _pendingY;
        private static string _pendingTail;      // ring counter (", 2 of 4.") or null

        // ---- Scan/list state ----
        private static int _lastRing = -1;
        private static bool _listOpen;
        private static List<Entry>[] _rings;
        private static int _listCat;
        private static int _listIdx = -1;
        private static int _swallowTailFrame = -1;

        // ---- Placement boundary state ----
        private static bool _lastValidKnown;
        private static bool _lastValid;
        private static bool _censusSpoken;

        // ---- Initiative panel mode (CP4, owner design 2026-08-18): I snaps
        //      onto the acting character's row; the panel's NATIVE hover
        //      machinery does the rest (slide-out, current-initiative static,
        //      map highlight). W/S re-snap rows; A/D/I/Escape exit with the
        //      cursor rerouted onto the hovered combatant's tile. Rows are
        //      rebuilt per frame — the mode holds an INDEX and re-snaps to
        //      the fresh element each frame. Clicks pass through untouched
        //      (ride test). ----
        private static bool _panelOpen;
        private static int _panelIdx = -1;
        private static object _panelSpokenChar;   // hover-line dedup (reference identity)

        /// <summary>Consulted by the movement suppression (WP9 modal
        /// precedent): W/S walk rows, A/D exit — none may move the character.</summary>
        public static bool PanelActive => _panelOpen && InCombat();

        // Our own virtual-mouse writes (latch assert, panel snap) must not
        // count as external takeovers.
        private static bool _selfAsserting;

        /// <summary>The yield discipline (owner ruling 2026-08-18): a
        /// deliberate GAME-side mouse placement (Ctrl row snap, popup snap,
        /// funnel snap) releases the cursor's hold exactly like a physical
        /// takeover — the latch never fights the game. Called from the mouse
        /// guard's setVirtualMousePosition postfix.</summary>
        internal static void NoteExternalMouseSet()
        {
            if (_selfAsserting) return;
            if (_held && InCombat()) _held = false;
        }

        public static bool ListOpen => _listOpen;

        /// <summary>Choke-point swallow (same contract as the overland
        /// cursor's): keys the game must not see while the K list is open.</summary>
        public static bool ShouldSwallowKey(KeyCode key)
        {
            // Panel mode: Escape is an exit, never the menu. (W/S/A/D are
            // covered by the movement suppression; I never reaches the game —
            // its controller-Y feed is combat-gated.)
            if (_panelOpen && key == KeyCode.Escape && InCombat()) return true;
            if (!_listOpen && Time.frameCount > _swallowTailFrame) return false;
            switch (key)
            {
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                case KeyCode.LeftArrow:
                case KeyCode.RightArrow:
                case KeyCode.Escape:
                case KeyCode.Backspace:
                case KeyCode.K:
                    return true;
                default:
                    return false;
            }
        }

        public static bool SuppressButtonB()
            => _listOpen || Time.frameCount <= _swallowTailFrame;

        /// <summary>Combat-scoped Y unbind (owner ruling): I is the initiative
        /// door in combat; the controller-Y feed suppresses there so the
        /// CHARACTER SHEET quick-button (Y's combat consumer — native C covers
        /// it) never fires from I. Popups keep Y — they read it as an option
        /// scheme slot (survey §4c).</summary>
        internal static bool SuppressButtonY()
            => InCombat() && !PopupUp();

        // ---- Gates and reads ----

        private static bool InCombat()
        {
            object state = Pump.CurrentStateObject();
            if (state == null) return false;
            return (Seams.CombatBaseStateType != null && Seams.CombatBaseStateType.IsInstanceOfType(state))
                || (Seams.CombatPlacementStateType != null && Seams.CombatPlacementStateType.IsInstanceOfType(state));
        }

        private static bool InPlacement()
        {
            object state = Pump.CurrentStateObject();
            return state != null && Seams.CombatPlacementStateType != null
                && Seams.CombatPlacementStateType.IsInstanceOfType(state);
        }

        private static object CurrentMap()
        {
            try
            {
                if (Seams.MainControl_getDataControl == null || Seams.DataControl_currentMap == null) return null;
                object dc = Seams.MainControl_getDataControl.Invoke(null, null);
                return dc == null ? null : Seams.DataControl_currentMap.GetValue(dc);
            }
            catch { return null; }
        }

        /// <summary>The acting character: the encounter's current character;
        /// during placement (current == null until Begin Combat) the current
        /// PC being placed.</summary>
        private static object ActingCharacter()
        {
            try
            {
                object dc = Seams.MainControl_getDataControl?.Invoke(null, null);
                if (dc == null) return null;
                object enc = Seams.DataControl_getCombatEncounter?.Invoke(dc, null);
                object cur = enc == null ? null
                    : Seams.CombatEncounter_getCurrentCharacter?.Invoke(enc, null);
                if (cur != null) return cur;
                return Seams.DataControl_getCurrentPC?.Invoke(dc, null);
            }
            catch { return null; }
        }

        private static bool AnchorPos(out int x, out int y)
        {
            x = 0; y = 0;
            try
            {
                object ch = ActingCharacter();
                if (ch == null || Seams.Character_getMapTile == null) return false;
                object tile = Seams.Character_getMapTile.Invoke(ch, null);
                if (tile == null) return false;
                x = (int)Seams.MapTile_getTileX.Invoke(tile, null);
                y = (int)Seams.MapTile_getTileY.Invoke(tile, null);
                return true;
            }
            catch { return false; }
        }

        private static object TileAt(object map, int x, int y)
        {
            try
            {
                if (Seams.Map_isTileValid == null || Seams.Map_getTile == null) return null;
                if (!(bool)Seams.Map_isTileValid.Invoke(map, new object[] { x, y })) return null;
                return Seams.Map_getTile.Invoke(map, new object[] { x, y });
            }
            catch { return null; }
        }

        private static bool B(System.Reflection.MethodInfo m, object target)
        {
            try { return m != null && target != null && (bool)m.Invoke(target, null); }
            catch { return false; }
        }

        private static string S(System.Reflection.MethodInfo m, object target)
        {
            try { return m == null || target == null ? null : m.Invoke(target, null) as string; }
            catch { return null; }
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

        // ---- The latch (called from Plugin.Update, after OverlandCursor) ----

        public static void Tick()
        {
            try
            {
                if (!InCombat()) return;
                object map = CurrentMap();
                if (map == null) return;

                // Encounter identity: a new fight resets the cursor silently.
                object enc = null;
                try
                {
                    object dc = Seams.MainControl_getDataControl?.Invoke(null, null);
                    enc = dc == null ? null : Seams.DataControl_getCombatEncounter?.Invoke(dc, null);
                }
                catch { }
                if (!ReferenceEquals(enc, _encounterSeen))
                {
                    _encounterSeen = enc;
                    Drop();
                    _lastValidKnown = false;
                    _censusSpoken = false;
                }

                // Placement census (config-gated, once per encounter).
                if (!_censusSpoken && InPlacement())
                {
                    _censusSpoken = true;
                    if (_cfgPlacementCensus != null && _cfgPlacementCensus.Value)
                    {
                        int n = ValidTiles(map)?.Count ?? -1;
                        if (n >= 0)
                            Scaffold.SpeechService.SayQueued(
                                n == 1 ? "1 valid tile." : $"{n} valid tiles.", "Nav");
                    }
                }

                if (PopupUp() || Patches.GridNavigationPatch.GridActive()) return;

                // Panel mode: the per-frame re-snap onto the focused row
                // (rows are rebuilt each frame; the game's own snap helper
                // reads live coordinates, so the slide-out stays hovered).
                if (_panelOpen)
                {
                    PanelResnap();
                    return;
                }

                if (!_held) return;
                AssertMouse(map);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[CombatCursor] {ex.Message}");
            }
        }

        // ---- Initiative panel mode ----

        private static object PanelRowElement(int idx, out object character, out int count)
        {
            character = null; count = 0;
            try
            {
                object state = Pump.CurrentStateObject();
                if (state == null || Seams.StateBase_guiControl == null
                    || Seams.GUIControl_initiativeList == null || Seams.UICanvas_getElements == null)
                    return null;
                object gui = Seams.StateBase_guiControl.GetValue(state);
                object list = gui == null ? null : Seams.GUIControl_initiativeList.GetValue(gui);
                if (list == null) return null;
                var elements = Seams.UICanvas_getElements.Invoke(list, null) as System.Collections.IList;
                if (elements == null || elements.Count == 0) return null;
                count = elements.Count;
                if (idx < 0 || idx >= elements.Count) return null;
                object row = elements[idx];
                if (row != null && Seams.InitiativeButton_getCharacter != null
                    && Seams.InitiativeButtonType != null
                    && Seams.InitiativeButtonType.IsInstanceOfType(row))
                    character = Seams.InitiativeButton_getCharacter.Invoke(row, null);
                return row;
            }
            catch { return null; }
        }

        private static void PanelResnap()
        {
            object row = PanelRowElement(_panelIdx, out object ch, out int count);
            if (row == null)
            {
                // Roster shrank under us (death re-sort): clamp or close.
                if (count == 0) { ClosePanel(rerouteTo: null); return; }
                _panelIdx = Math.Min(_panelIdx, count - 1);
                row = PanelRowElement(_panelIdx, out ch, out count);
                if (row == null) { ClosePanel(rerouteTo: null); return; }
            }
            SnapToRow(row);
            // The hover line speaks when the focused COMBATANT changes
            // (reference identity — rows are rebuilt per frame, characters
            // are stable).
            if (!ReferenceEquals(ch, _panelSpokenChar) && ch != null)
            {
                _panelSpokenChar = ch;
                SpeakPanelRow(ch, _panelIdx, count);
            }
        }

        private static void SnapToRow(object rowElement)
        {
            try
            {
                object state = Pump.CurrentStateObject();
                object gui = state == null ? null : Seams.StateBase_guiControl?.GetValue(state);
                if (gui == null || Seams.GUIControl_setMouseToUIElement == null) return;
                _selfAsserting = true;
                try
                {
                    // The popup-grid family's own hover offset (+8, -8).
                    Seams.GUIControl_setMouseToUIElement.Invoke(gui, new object[] { rowElement, 8, -8 });
                }
                finally { _selfAsserting = false; }
            }
            catch { }
        }

        private static void SpeakPanelRow(object ch, int idx, int count)
        {
            string name = CombatSpine.DisplayNameOf(ch) ?? "Someone";
            string status = null;
            try
            {
                string raw = Seams.Character_printInitiativeStatus?.Invoke(ch, null) as string;
                if (!string.IsNullOrWhiteSpace(raw))
                    status = Patches.TextCleaner.CleanText(raw).Trim().TrimStart('(').TrimEnd(')').ToLowerInvariant();
            }
            catch { }
            int vit = -1, wounds = -1;
            try
            {
                if (Seams.Character_getVitality != null && Seams.Character_getWounds != null)
                {
                    vit = (int)Seams.Character_getVitality.Invoke(ch, null);
                    wounds = (int)Seams.Character_getWounds.Invoke(ch, null);
                }
            }
            catch { }
            string vitals = vit >= 0 ? $", {vit} vitality, {wounds} wounds" : "";
            string statusPart = string.IsNullOrWhiteSpace(status) ? "" : $", {status}";
            Scaffold.SpeechService.Say($"{name}{statusPart}{vitals}, {idx + 1} of {count}.", "Nav");
        }

        private static void OpenPanel()
        {
            // Land on the acting character's row; top of order as fallback.
            object actor = ActingCharacter();
            int startIdx = 0;
            PanelRowElement(0, out _, out int count);
            if (count == 0)
            {
                Scaffold.SpeechService.Say("No initiative panel.", "Nav");
                return;
            }
            if (actor != null)
            {
                for (int i = 0; i < count; i++)
                {
                    PanelRowElement(i, out object ch, out _);
                    if (ReferenceEquals(ch, actor)) { startIdx = i; break; }
                }
            }
            _held = false;              // the panel owns the mouse; the tile hold yields
            _pendingSpeak = false;
            CloseListSilent();
            _panelOpen = true;
            _panelIdx = startIdx;
            _panelSpokenChar = null;
            PanelResnap();
        }

        /// <summary>Exit reroutes the battlefield cursor onto the hovered
        /// combatant's tile (the row points at them by construction) — the
        /// standard landing line speaks there (owner design).</summary>
        private static void ClosePanel(object rerouteTo)
        {
            _panelOpen = false;
            _panelSpokenChar = null;
            if (rerouteTo != null)
            {
                try
                {
                    object tile = Seams.Character_getMapTile?.Invoke(rerouteTo, null);
                    if (tile != null)
                    {
                        _tileX = (int)Seams.MapTile_getTileX.Invoke(tile, null);
                        _tileY = (int)Seams.MapTile_getTileY.Invoke(tile, null);
                        _held = true;
                        object map = CurrentMap();
                        if (map != null) { AssertMouse(map); QueueLanding(null); }
                        return;
                    }
                }
                catch { }
            }
        }

        private static bool ProcessPanelInput()
        {
            if (Input.GetKeyDown(KeyCode.W))
            {
                if (_panelIdx > 0) { _panelIdx--; _panelSpokenChar = null; }
                else Scaffold.SpeechService.Say("Top.", "Nav");
                return true;
            }
            if (Input.GetKeyDown(KeyCode.S))
            {
                PanelRowElement(_panelIdx, out _, out int count);
                if (_panelIdx < count - 1) { _panelIdx++; _panelSpokenChar = null; }
                else Scaffold.SpeechService.Say("Bottom.", "Nav");
                return true;
            }
            if (Input.GetKeyDown(KeyCode.I) || Input.GetKeyDown(KeyCode.Escape)
                || Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.D))
            {
                PanelRowElement(_panelIdx, out object ch, out _);
                ClosePanel(ch);
                return true;
            }
            return false;
        }

        /// <summary>Same viewport→virtual-screen geometry as the overland
        /// latch (the map machinery is identical; combat clamps the
        /// battlefield to this same window).</summary>
        private static void AssertMouse(object map)
        {
            if (Seams.SkaldIO_setVirtualMousePosition == null
                || Seams.Map_getViewportX == null || Seams.Map_getViewportY == null) return;

            int vx = (int)Seams.Map_getViewportX.Invoke(map, null);
            int vy = (int)Seams.Map_getViewportY.Invoke(map, null);

            int c = _tileX - vx + 12;
            int r = _tileY - vy + 9;
            if (c < 1 || c > 23 || r < 1 || r > 17) return;

            int scrollX = 16, scrollY = 16;
            try
            {
                if (Seams.Map_mapIllustrator != null && Seams.MapIllustrator_scrollControl != null
                    && Seams.ScrollControl_scrollX != null && Seams.ScrollControl_scrollY != null)
                {
                    object ill = Seams.Map_mapIllustrator.GetValue(map);
                    object sc = ill != null ? Seams.MapIllustrator_scrollControl.GetValue(ill) : null;
                    if (sc != null)
                    {
                        scrollX = (int)Seams.ScrollControl_scrollX.GetValue(sc);
                        scrollY = (int)Seams.ScrollControl_scrollY.GetValue(sc);
                    }
                }
            }
            catch { }

            int mx = (12 - scrollX) + 16 * c + 8;
            int my = -scrollY + 16 * r + 8;
            try
            {
                _selfAsserting = true;
                Seams.SkaldIO_setVirtualMousePosition.Invoke(null, new object[] { mx, my });
            }
            finally { _selfAsserting = false; }
        }

        private static void Drop()
        {
            _held = false;
            _pendingSpeak = false;
            _panelOpen = false;
            _panelSpokenChar = null;
            CloseListSilent();
        }

        /// <summary>From the state clock: the cursor survives intra-combat
        /// churn (planning↔resolve↔targeting↔continue) and dies on leaving
        /// the combat family.</summary>
        public static void OnStateTransition()
        {
            if (!InCombat()) Drop();
        }

        // ---- Input (called from InputHandler after the overland cursor) ----

        public static bool ProcessInput()
        {
            if (!InCombat()) return false;
            object map = CurrentMap();
            if (map == null || PopupUp() || Patches.GridNavigationPatch.GridActive()) return false;

            // Panel mode owns W/S (rows) and A/D/I/Escape (exits) while open.
            if (_panelOpen && ProcessPanelInput()) return true;
            if (_panelOpen) return false;   // clicks and other keys pass through untouched

            if (Input.GetKeyDown(KeyCode.I)) { OpenPanel(); return true; }

            if (_listOpen && ProcessListInput(map)) return true;

            if (Input.GetKeyDown(KeyCode.K)) { OpenList(map); return true; }

            if (!_listOpen)
            {
                if (Input.GetKeyDown(KeyCode.UpArrow)) { Nudge(map, 0, 1); return true; }
                if (Input.GetKeyDown(KeyCode.DownArrow)) { Nudge(map, 0, -1); return true; }
                if (Input.GetKeyDown(KeyCode.LeftArrow)) { Nudge(map, -1, 0); return true; }
                if (Input.GetKeyDown(KeyCode.RightArrow)) { Nudge(map, 1, 0); return true; }
            }

            if (Input.GetKeyDown(KeyCode.H)) { Scan(map, Ring.Hostiles); return true; }
            if (Input.GetKeyDown(KeyCode.N)) { Scan(map, Ring.Friendlies); return true; }
            if (Input.GetKeyDown(KeyCode.P)) { ScanReverse(map); return true; }

            return false;
        }

        private static void Nudge(object map, int dx, int dy)
        {
            ClearTooltip();
            if (!AnchorPos(out int ax, out int ay)) return;

            if (!_held)
            {
                _held = true;
                _tileX = ax;
                _tileY = ay;
            }
            else
            {
                int nx = _tileX + dx, ny = _tileY + dy;
                int vx = (int)Seams.Map_getViewportX.Invoke(map, null);
                int vy = (int)Seams.Map_getViewportY.Invoke(map, null);
                if (nx < vx - 11 || nx > vx + 11 || ny < vy - 8 || ny > vy + 8)
                {
                    Scaffold.SpeechService.Say("Edge of view.", "Nav");
                    return;
                }
                _tileX = nx;
                _tileY = ny;
            }

            AssertMouse(map);
            QueueLanding(null);
        }

        /// <summary>Arm the deferred landing: the game's next update re-hovers
        /// the new tile and recomputes the course; the drain then speaks from
        /// settled truth. An unspoken pending landing from an EARLIER frame
        /// flushes first (its course settled and is still current — the new
        /// mouse move hasn't been seen by a game update yet), so fast key
        /// repeats never drop a landing (review find 6). Same-frame re-arms
        /// are genuine last-wins.</summary>
        private static void QueueLanding(string tail)
        {
            if (_pendingSpeak && Time.frameCount > _pendingFrame)
            {
                _pendingSpeak = false;
                object map = CurrentMap();
                if (map != null && InCombat())
                    SpeakTile(map, _pendingX, _pendingY, _pendingTail);
            }
            _pendingSpeak = true;
            _pendingFrame = Time.frameCount;
            _pendingX = _tileX;
            _pendingY = _tileY;
            _pendingTail = tail;
        }

        /// <summary>Called from Pump.Drain each frame. Speaks the armed
        /// landing once a full game update has run since the nudge. Popups
        /// and open selector grids DEFER the speak (never drop) — the game's
        /// own mouse branch skips the hover/course recompute while a selector
        /// is open, so the read would be unsettled, and speaking would break
        /// hold-and-flush against the popup's own announcement (review find 3).</summary>
        public static void DrainSpeak()
        {
            if (!_pendingSpeak || Time.frameCount <= _pendingFrame) return;
            if (!InCombat() || !_held) { _pendingSpeak = false; return; }
            if (PopupUp() || Patches.GridNavigationPatch.GridActive()) return;   // defer, not drop
            _pendingSpeak = false;
            object map = CurrentMap();
            if (map == null) return;
            SpeakTile(map, _pendingX, _pendingY, _pendingTail);
        }

        // ---- The unified landing readout ----

        private static void SpeakTile(object map, int tx, int ty, string countTail)
        {
            if (!AnchorPos(out int ax, out int ay)) { ax = tx; ay = ty; }
            object tile = TileAt(map, tx, ty);
            if (tile == null)
            {
                Scaffold.SpeechService.Say("Nothing." + Offset(tx - ax, ty - ay), "Nav");
                return;
            }

            if (!B(Seams.MapTile_isSpotted, tile))
            {
                Scaffold.SpeechService.Say("Unexplored." + Offset(tx - ax, ty - ay) + (countTail ?? ""), "Nav");
                return;
            }

            string label = TileLabel(tile);
            string offset = Offset(tx - ax, ty - ay);
            string fact = "";
            string valid = "";

            if (InPlacement())
            {
                bool isValid = IsValidPlacement(map, tile);
                // The perimeter crossing speaks itself (owner ruling).
                string crossing = "";
                if (_lastValidKnown && _lastValid && !isValid) crossing = "Out of bounds. ";
                _lastValidKnown = true;
                _lastValid = isValid;
                valid = isValid ? "Valid" : "Invalid";
                bool lead = _cfgValidPrepend == null || _cfgValidPrepend.Value;
                string body = lead ? valid + ", " + label : label + ", " + valid.ToLowerInvariant();
                Scaffold.SpeechService.Say(
                    crossing + body + offset + LightTail(tile) + (countTail ?? ""), "Nav");
                NoteInspect(tile);
                return;
            }

            fact = PathFact(map, tile, tx, ty, ax, ay);
            Scaffold.SpeechService.Say(label + offset + fact + LightTail(tile) + (countTail ?? ""), "Nav");
            NoteInspect(tile);
        }

        /// <summary>The combat label ladder: the overland rendered-priority
        /// ladder with combat semantics — lettered occupants (uniform
        /// identifiers, owner mandate), the burning-tile label, corpse
        /// naming, then the tile's own data name and flag fallbacks.</summary>
        private static string TileLabel(object tile)
        {
            object ch = null;
            try { ch = Seams.MapTile_getLiveCharacter?.Invoke(tile, null); } catch { }
            if (ch != null)
            {
                bool identifiable = B(Seams.MapTile_isIlluminated, tile) || B(Seams.Character_isSpotted, ch)
                    || B(Seams.Character_isPC, ch);
                if (identifiable)
                {
                    string dn = CombatSpine.DisplayNameOf(ch);
                    if (!string.IsNullOrWhiteSpace(dn)) return dn;
                }
                if (!B(Seams.MapTile_isConcealment, tile)) return "Something unseen";
            }

            object prop = null;
            try { prop = Seams.MapTile_getPropOrGuestProp?.Invoke(tile, null); } catch { }
            if (prop != null && !B(Seams.Prop_isHidden, prop) && !B(Seams.Prop_shouldNotBeDrawn, prop))
            {
                string pn = Patches.TextCleaner.CleanText(S(Seams.SkaldBaseObject_getName, prop) ?? "");
                if (!string.IsNullOrWhiteSpace(pn)) return pn;
            }

            // Burning tiles: the cosmetic fire object is rendered — it joins
            // the ladder (survey §7b; render-first).
            try
            {
                object fire = Seams.MapTile_getMapObject?.Invoke(tile, null);
                if (fire != null && Seams.MapObjectFireType != null
                    && Seams.MapObjectFireType.IsInstanceOfType(fire)
                    && !B(Seams.TempMapObject_isDead, fire))
                    return "Fire";
            }
            catch { }

            object dead = null;
            try { dead = Seams.MapTile_getDeadParty?.Invoke(tile, null); } catch { }
            if (dead != null)
            {
                string dn = Patches.TextCleaner.CleanText(S(Seams.SkaldBaseObject_getName, dead) ?? "");
                return string.IsNullOrWhiteSpace(dn) ? "Corpse" : "Corpse of " + dn;
            }

            string tn = Patches.TextCleaner.CleanText(S(Seams.SkaldBaseObject_getName, tile) ?? "");
            if (!string.IsNullOrWhiteSpace(tn)) return tn;

            if (B(Seams.MapTile_isWater, tile)) return "Water";
            if (B(Seams.MapTile_isVoidTile, tile)) return "Nothing";
            if (!B(Seams.MapTile_isPassable, tile)) return "Blocked";
            return "Open";
        }

        /// <summary>The path/cost fact (owner grammar: costs before the fact,
        /// turn-enders named). Reads the acting character's own
        /// hover-recomputed course — settled by the deferral. The disengage
        /// and swap truths come from the game's own flags; the pips mispaint
        /// both (survey §4b.17).</summary>
        private static string PathFact(object map, object tile, int tx, int ty, int ax, int ay)
        {
            try
            {
                // Only CombatPlanningState's own mouse branch recomputes the
                // course — in targeting/resolve/continue the read would be
                // STALE (their setMousePosition drives the template, not the
                // path). Swap/disengage forecasts are course-independent and
                // planning-only decisions anyway.
                object state = Pump.CurrentStateObject();
                if (state == null || Seams.CombatPlanningStateType == null
                    || !Seams.CombatPlanningStateType.IsInstanceOfType(state)) return "";
                object actor = ActingCharacter();
                if (actor == null) return "";
                if (tx == ax && ty == ay) return "";
                bool actorIsPC = B(Seams.Character_isPC, actor);
                if (!actorIsPC) return "";   // path facts only for the player's own planning

                object occ = null;
                try { occ = Seams.MapTile_getLiveCharacter?.Invoke(tile, null); } catch { }

                bool inMelee = B(Seams.Character_isInMelee, actor)
                    && !FlagOn(actor, Seams.CombatAbilityFlags_evasion);

                if (occ != null)
                {
                    // The game's own swap-eligibility test: any non-hostile
                    // occupant swaps — allied NPCs included (review find 4).
                    bool hostileToActor = IsNPCHostile(actor, occ);
                    if (!hostileToActor)
                        return FlagOn(actor, Seams.CombatAbilityFlags_freeSwap)
                            ? ", swap costs 1 move" : ", swap ends turn";
                    // A hostile in melee range: clicking attacks in place, no
                    // movement, no disengage. Beyond melee range the approach
                    // IS a move — the disengage truth applies.
                    int cheb = Math.Max(Math.Abs(tx - ax), Math.Abs(ty - ay));
                    bool orthAdjacent = Math.Abs(tx - ax) + Math.Abs(ty - ay) == 1;
                    if (orthAdjacent) return "";
                    if (inMelee) return ", moving disengages, ends turn";
                }
                else if (inMelee)
                {
                    // Disengage: any move out of melee without evasion wipes the turn.
                    return ", moving disengages, ends turn";
                }

                int len = CourseLength(actor, tx, ty);
                if (len <= 0)
                {
                    // No course terminating at THIS tile = unreachable (a
                    // failed hover recompute leaves the previous course in
                    // place — the destination check is the guard, review
                    // find 1).
                    if (occ == null && B(Seams.MapTile_isPassable, tile) && !B(Seams.MapTile_isWater, tile))
                        return ", no path";
                    return "";
                }
                int reach = ReachBudget(actor);
                string steps = len == 1 ? "path 1 step" : $"path {len} steps";
                if (reach >= len) return $", {steps}, in reach";
                if (reach <= 0) return $", {steps}, none in reach";
                return $", {steps}, {reach} in reach";
            }
            catch { return ""; }
        }

        /// <summary>Course length ONLY when the course actually terminates at
        /// (tx, ty): a failed hover recompute writes nothing and leaves the
        /// previous tile's course behind (NavigationTools.setPath sets only on
        /// success; clears only at round/combat boundaries) — trusting the
        /// bare length would speak a confident path fact for an unreachable
        /// tile (review find 1).</summary>
        private static int CourseLength(object actor, int tx, int ty)
        {
            try
            {
                if (Seams.Character_GetNavigationCourse == null || Seams.NavigationCourse_getLength == null)
                    return -1;
                object course = Seams.Character_GetNavigationCourse.Invoke(actor, null);
                if (course == null) return -1;
                if (Seams.NavigationCourse_getDestination != null)
                {
                    object dest = Seams.NavigationCourse_getDestination.Invoke(course, null);
                    if (dest == null || !PointMatches(dest, tx, ty)) return -1;
                }
                return (int)Seams.NavigationCourse_getLength.Invoke(course, null);
            }
            catch { return -1; }
        }

        private static bool PointMatches(object point, int x, int y)
        {
            try
            {
                var t = point.GetType();
                var fx = HarmonyLib.AccessTools.Field(t, "X") ?? HarmonyLib.AccessTools.Field(t, "x");
                var fy = HarmonyLib.AccessTools.Field(t, "Y") ?? HarmonyLib.AccessTools.Field(t, "y");
                if (fx != null && fy != null)
                    return Convert.ToInt32(fx.GetValue(point)) == x && Convert.ToInt32(fy.GetValue(point)) == y;
                var px = HarmonyLib.AccessTools.Property(t, "X") ?? HarmonyLib.AccessTools.Property(t, "x");
                var py = HarmonyLib.AccessTools.Property(t, "Y") ?? HarmonyLib.AccessTools.Property(t, "y");
                if (px != null && py != null)
                    return Convert.ToInt32(px.GetValue(point, null)) == x && Convert.ToInt32(py.GetValue(point, null)) == y;
                return false;
            }
            catch { return false; }
        }

        private static bool IsNPCHostile(object actor, object other)
        {
            try
            {
                return Seams.Character_isNPCHostile != null
                    && (bool)Seams.Character_isNPCHostile.Invoke(actor, new[] { other });
            }
            catch { return true; }   // fail toward NOT promising a swap
        }

        private static int ReachBudget(object actor)
        {
            try
            {
                if (Seams.Character_getExactRemainingCombatMovesIncludingAttacks == null) return -1;
                return (int)Seams.Character_getExactRemainingCombatMovesIncludingAttacks.Invoke(actor, null);
            }
            catch { return -1; }
        }

        private static bool FlagOn(object character, System.Reflection.FieldInfo flag)
        {
            try
            {
                if (flag == null || Seams.Character_dynamicData == null
                    || Seams.DynamicData_combatAbilityFlags == null) return false;
                object dyn = Seams.Character_dynamicData.GetValue(character);
                object flags = dyn == null ? null : Seams.DynamicData_combatAbilityFlags.GetValue(dyn);
                return flags != null && (bool)flag.GetValue(flags);
            }
            catch { return false; }
        }

        private static string LightTail(object tile)
        {
            try
            {
                if (_cfgLight == null || !_cfgLight.Value || Seams.MapTile_getLightLevel == null)
                    return "";
                float light = (float)Seams.MapTile_getLightLevel.Invoke(tile, null);
                string s = light.ToString("0.0#");
                return $", light {s}";
            }
            catch { return ""; }
        }

        private static bool IsValidPlacement(object map, object tile)
        {
            var valid = ValidTiles(map);
            return valid != null && valid.Contains(tile);
        }

        private static System.Collections.IList ValidTiles(object map)
        {
            try
            {
                if (Seams.Map_getPreCombatPlacementTiles == null) return null;
                return Seams.Map_getPreCombatPlacementTiles.Invoke(map, null) as System.Collections.IList;
            }
            catch { return null; }
        }

        private static string Offset(int dx, int dy)
        {
            if (dx == 0 && dy == 0) return ", here.";
            var parts = new List<string>(2);
            if (dy > 0) parts.Add($"{dy} north");
            else if (dy < 0) parts.Add($"{-dy} south");
            if (dx > 0) parts.Add($"{dx} east");
            else if (dx < 0) parts.Add($"{-dx} west");
            return ", " + string.Join(", ", parts.ToArray()) + ".";
        }

        private static void NoteInspect(object tile)
        {
            string inspect = S(Seams.MapTile_getInspectDescription, tile);
            if (!string.IsNullOrWhiteSpace(inspect)) ReviewLayer.NotePanel("Inspect", inspect);
        }

        private static void ClearTooltip()
        {
            try
            {
                if (Seams.ToolTipPrinter_hasToolTip != null && (bool)Seams.ToolTipPrinter_hasToolTip.Invoke(null, null))
                    Seams.ToolTipPrinter_clearToolTip?.Invoke(null, null);
            }
            catch { }
        }

        // ---- Rings: hostiles / friendlies only (owner ruling) ----

        private static List<Entry> BuildRing(object map, Ring cat, int ax, int ay)
        {
            var ring = new List<Entry>();
            if (Seams.Map_getViewportX == null) return ring;
            int vx = (int)Seams.Map_getViewportX.Invoke(map, null);
            int vy = (int)Seams.Map_getViewportY.Invoke(map, null);
            var unseenTail = new List<Entry>();

            for (int tx = vx - 11; tx <= vx + 11; tx++)
            {
                for (int ty = vy - 8; ty <= vy + 8; ty++)
                {
                    object tile = TileAt(map, tx, ty);
                    if (tile == null || !B(Seams.MapTile_isSpotted, tile)) continue;

                    object ch = null;
                    try { ch = Seams.MapTile_getLiveCharacter?.Invoke(tile, null); } catch { }
                    if (ch == null) continue;

                    int dist = Math.Abs(tx - ax) + Math.Abs(ty - ay);
                    bool isPC = B(Seams.Character_isPC, ch);
                    bool identifiable = B(Seams.MapTile_isIlluminated, tile)
                        || B(Seams.Character_isSpotted, ch) || isPC;
                    if (!identifiable)
                    {
                        // Unseen-icon state tails the hostiles ring (caution bias).
                        if (cat == Ring.Hostiles && !B(Seams.MapTile_isConcealment, tile))
                            unseenTail.Add(new Entry { X = tx, Y = ty, Label = "Something unseen", Dist = dist });
                        continue;
                    }
                    bool hostile = B(Seams.Character_isHostile, ch);
                    if ((cat == Ring.Hostiles) != hostile) continue;
                    string name = CombatSpine.DisplayNameOf(ch)
                        ?? Patches.TextCleaner.CleanText(S(Seams.SkaldBaseObject_getName, ch) ?? "Someone");
                    ring.Add(new Entry { X = tx, Y = ty, Label = name, Dist = dist });
                }
            }

            ring.Sort((a, b) => a.Dist != b.Dist ? a.Dist - b.Dist : (a.Y != b.Y ? b.Y - a.Y : a.X - b.X));
            if (unseenTail.Count > 0)
            {
                unseenTail.Sort((a, b) => a.Dist - b.Dist);
                ring.AddRange(unseenTail);
            }
            return ring;
        }

        private static void Scan(object map, Ring cat)
        {
            ClearTooltip();
            _lastRing = (int)cat;
            if (!AnchorPos(out int ax, out int ay)) return;
            var ring = BuildRing(map, cat, ax, ay);
            if (ring.Count == 0)
            {
                Scaffold.SpeechService.Say($"No {RingNames[(int)cat]}.", "Nav");
                return;
            }
            int at = _held ? ring.FindIndex(e => e.X == _tileX && e.Y == _tileY) : -1;
            int next = at < 0 ? 0 : (at + 1) % ring.Count;
            LandOn(map, ring[next], next, ring.Count);
        }

        private static void ScanReverse(object map)
        {
            ClearTooltip();
            if (_lastRing < 0) { Scaffold.SpeechService.Say("No scan yet.", "Nav"); return; }
            if (!AnchorPos(out int ax, out int ay)) return;
            var ring = BuildRing(map, (Ring)_lastRing, ax, ay);
            if (ring.Count == 0)
            {
                Scaffold.SpeechService.Say($"No {RingNames[_lastRing]}.", "Nav");
                return;
            }
            int at = _held ? ring.FindIndex(e => e.X == _tileX && e.Y == _tileY) : -1;
            int prev = at < 0 ? ring.Count - 1 : (at - 1 + ring.Count) % ring.Count;
            LandOn(map, ring[prev], prev, ring.Count);
        }

        /// <summary>Ring/list landings use the IDENTICAL standard readout
        /// (owner ruling) plus the trailing counter.</summary>
        private static void LandOn(object map, Entry e, int idx, int count)
        {
            _held = true;
            _tileX = e.X;
            _tileY = e.Y;
            AssertMouse(map);
            QueueLanding($" {idx + 1} of {count}.");
        }

        // ---- K catalog: frozen two-ring census + browse ----

        private static void OpenList(object map)
        {
            ClearTooltip();
            if (!AnchorPos(out int ax, out int ay)) return;
            var rings = new[] { BuildRing(map, Ring.Hostiles, ax, ay), BuildRing(map, Ring.Friendlies, ax, ay) };
            if (rings[0].Count == 0 && rings[1].Count == 0)
            {
                // Nothing to browse: never enter list mode (review find 5 —
                // an empty modal just eats an extra escape press).
                Scaffold.SpeechService.Say("No combatants in view.", "Nav");
                return;
            }
            _rings = rings;
            _listOpen = true;
            _listCat = _rings[0].Count > 0 ? 0 : 1;
            _listIdx = -1;
            Scaffold.SpeechService.Say(
                $"{CountPhrase(_rings[0].Count, "hostile")}, {CountPhrase(_rings[1].Count, "friendly", "friendlies")}. Browse with arrows.",
                "Nav");
        }

        private static string CountPhrase(int n, string singular, string plural = null)
            => n == 1 ? $"1 {singular}" : $"{n} {plural ?? singular + "s"}";

        private static bool ProcessListInput(object map)
        {
            // Z = the native click on the landed tile: the list closes
            // SILENTLY and the press falls through (review find 2 — the
            // ride-verified overland discipline; a frozen census must never
            // survive the action it just launched).
            if (Input.GetKeyDown(KeyCode.Z))
            {
                ClearTooltip();
                CloseListSilent();
                return false;
            }
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace)
                || Input.GetKeyDown(KeyCode.K))
            {
                CloseList();
                return true;
            }
            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow))
            {
                _listCat = 1 - _listCat;
                _listIdx = -1;
                Scaffold.SpeechService.Say(
                    $"{RingNames[_listCat]}, {_rings[_listCat].Count}.", "Nav");
                return true;
            }
            if (Input.GetKeyDown(KeyCode.DownArrow)) { ListStep(map, 1); return true; }
            if (Input.GetKeyDown(KeyCode.UpArrow)) { ListStep(map, -1); return true; }
            return false;
        }

        private static void ListStep(object map, int dir)
        {
            var ring = _rings[_listCat];
            if (ring.Count == 0)
            {
                Scaffold.SpeechService.Say($"No {RingNames[_listCat]}.", "Nav");
                return;
            }
            _listIdx = _listIdx < 0
                ? (dir > 0 ? 0 : ring.Count - 1)
                : Mathf.Clamp(_listIdx + dir, 0, ring.Count - 1);
            LandOn(map, ring[_listIdx], _listIdx, ring.Count);
        }

        private static void CloseList()
        {
            CloseListSilent();
            _swallowTailFrame = Time.frameCount + 1;
            Scaffold.SpeechService.Say("Closed.", "Nav");
        }

        private static void CloseListSilent()
        {
            _listOpen = false;
            _rings = null;
            _listIdx = -1;
        }
    }
}

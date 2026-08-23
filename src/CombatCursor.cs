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
            _cfgValidPrepend = config.Bind("Combat", "PlacementQualifierLeads", false,
                "During deployment, speak Valid/Invalid before the tile label (default false — "
                + "validity TRAILS tile data, owner ruling 2026-08-23).");
            _cfgPlacementCensus = config.Bind("Combat", "PlacementCensus", false,
                "Speak the number of valid deployment tiles when the deploy screen opens.");
        }

        private enum Ring { Hostiles, Friendlies, NeutralsFriendlies, Party }
        private static readonly string[] RingNames = { "hostiles", "friendlies", "neutrals and friendlies", "party" };

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
        private static string _pendingPrefix;    // initiative ordinal ("3, ") or null

        // ---- The K tables (Layer 2, table-ui-design §6.18): the latched
        //      four-tab instrument. _listOpen = the latch. Tabs are LIVE —
        //      rows rebuild from game truth on every keypress (the mutation
        //      clock is the player's own hand); only the per-tab row cursor
        //      persists. The latch owns WASD while open (tab swap): the
        //      binding route is swallowed at the SkaldIO choke
        //      (ShouldSwallowKey) AND the stick-read booleans are forced
        //      false at the feed layer (LatchClaimsStick) — the ruled
        //      patch shape, gate receipt 7. ----
        private static bool _listOpen;
        private static int _tabIdx;                    // 0 TurnOrder · 1 Hostiles · 2 Neutrals/Friendlies · 3 Party
        private static readonly int[] _tabRow = { -1, -1, -1, -1 };
        private static int _swallowTailFrame = -1;
        private static bool _latchGateLogged;

        private static readonly string[] TabNames =
            { "Turn Order", "Hostiles", "Neutrals and friendlies", "Party" };

        // (LatchClaimsStick retired — nav revision §5, owner ruling
        //  2026-08-23: WASD is RELEASED under the latch to native character
        //  stepping, the overland walk-while-browsing model. The protective
        //  swallows on numbers/Space stand; movement is intent.)

        // ---- Placement boundary state ----
        private static bool _lastValidKnown;
        private static bool _lastValid;
        private static bool _censusSpoken;

        // (The CP4 initiative panel mode retired 2026-08-21 — combat Layer 1,
        // table-ui-design §6.18: the I/U initiative walk parks the cursor on
        // actors' battlefield tiles through the normal hover path, so the
        // native list's fences — per-frame rebuild, sustained hover, static
        // map-highlight mutation — no longer apply to anything.)

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
            if (!_listOpen && Time.frameCount > _swallowTailFrame) return false;
            // The latch suspends under the game's own modal surfaces (Sonnet
            // MUST-FIX): a popup or selector grid over the latch keeps full
            // native input — Escape must reach the grid, options must fire.
            if (_listOpen && (PopupUp() || Patches.GridNavigationPatch.GridActive())) return false;
            switch (key)
            {
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                case KeyCode.LeftArrow:
                case KeyCode.RightArrow:
                case KeyCode.Escape:
                case KeyCode.Backspace:
                case KeyCode.K:
                // (WASD released — nav revision §5: native character
                // stepping while the latch is open, deliberate by nature.)
                // Protective mandate (Sonnet note, adopted): option-row
                // numbers and Space (the MainInteract alias — the contextual
                // ATTACK button) must not spend the turn from inside a
                // browse. Z remains the one deliberate act.
                case KeyCode.Alpha1: case KeyCode.Alpha2: case KeyCode.Alpha3:
                case KeyCode.Alpha4: case KeyCode.Alpha5: case KeyCode.Alpha6:
                case KeyCode.Alpha7: case KeyCode.Alpha8: case KeyCode.Alpha9:
                case KeyCode.Space:
                    return true;
                default:
                    return false;
            }
        }

        // (SuppressButtonB / SuppressButtonY retired 2026-08-21: R16 unbound
        // the face-button keyboard feeds mod-wide — there is no B or Y feed
        // left to suppress.)

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

                if (!_held) return;
                AssertMouse(map);
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("CombatCursor", ex.Message);
            }
        }

        // ---- The initiative walk (combat Layer 1, table-ui-design §6.18):
        //      I/U step the encounter's own initiative order forward/backward,
        //      parking the cursor on each actor's tile through the normal
        //      hover path; the landing line leads with the actor's ordinal —
        //      "3, Goblin B, …" — the temporal walk's leading fact (rank is
        //      conveyed by list order in the game's own UI). The order is
        //      read FRESH per press: InitiativeList.getInitiativeList() via
        //      the one private hop (CombatEncounter.initiativeList) — the
        //      list instance is swapped wholesale on every sort (survey
        //      2026-08-21), so nothing is cached; dead entries are filtered
        //      here (the game's sort drops them only lazily). ----

        /// <summary>The live initiative order: dead-filtered, tile-bearing
        /// actors only (a deploying PC with no tile yet cannot host the
        /// cursor). Fresh snapshot per call.</summary>
        private static List<object> InitiativeOrder()
        {
            var order = new List<object>();
            try
            {
                object dc = Seams.MainControl_getDataControl?.Invoke(null, null);
                object enc = dc == null ? null : Seams.DataControl_getCombatEncounter?.Invoke(dc, null);
                if (enc == null || Seams.CombatEncounter_initiativeList == null
                    || Seams.InitiativeList_getInitiativeList == null) return order;
                object il = Seams.CombatEncounter_initiativeList.GetValue(enc);
                var list = il == null ? null
                    : Seams.InitiativeList_getInitiativeList.Invoke(il, null) as System.Collections.IList;
                if (list == null) return order;
                foreach (object ch in list)
                {
                    if (ch == null || B(Seams.Character_isDead, ch)) continue;
                    if (TileOf(ch) == null) continue;
                    order.Add(ch);
                }
            }
            catch { }
            return order;
        }

        private static object TileOf(object ch)
        {
            try { return Seams.Character_getMapTile?.Invoke(ch, null); }
            catch { return null; }
        }

        private static int IndexOfRef(List<object> list, object item)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], item)) return i;
            return -1;
        }

        /// <summary>I (+1) / U (−1). Scan-family grammar: anchored on the
        /// combatant under the cursor, a press steps (wrapping); unanchored,
        /// the first press LANDS on the acting character without stepping
        /// (top/tail of order when no one is acting, e.g. placement).</summary>
        private static void StepInitiative(object map, int dir)
        {
            ClearTooltip();
            var order = InitiativeOrder();
            if (order.Count == 0)
            {
                Scaffold.SpeechService.Say("No initiative order.", "Nav");
                return;
            }

            int at = -1;
            if (_held)
            {
                object tile = TileAt(map, _tileX, _tileY);
                object occ = null;
                try { occ = tile == null ? null : Seams.MapTile_getLiveCharacter?.Invoke(tile, null); }
                catch { }
                if (occ != null) at = IndexOfRef(order, occ);
            }

            int next;
            if (at >= 0)
            {
                next = ((at + dir) % order.Count + order.Count) % order.Count;
            }
            else
            {
                object actor = ActingCharacter();
                int cur = actor == null ? -1 : IndexOfRef(order, actor);
                next = cur >= 0 ? cur : (dir > 0 ? 0 : order.Count - 1);
            }
            LandOnCharacter(map, order[next], next + 1);
        }

        /// <summary>True when (tx,ty) lies inside the viewport window
        /// AssertMouse can actually park on (its own clamp, extracted —
        /// Sonnet MUST-FIX 2026-08-21: an off-viewport park silently no-ops,
        /// and announcing a landing that never happened desyncs the spoken
        /// position from Z's real click target).</summary>
        private static bool InParkWindow(object map, int tx, int ty)
        {
            try
            {
                int vx = (int)Seams.Map_getViewportX.Invoke(map, null);
                int vy = (int)Seams.Map_getViewportY.Invoke(map, null);
                int c = tx - vx + 12;
                int r = ty - vy + 9;
                return c >= 1 && c <= 23 && r >= 1 && r <= 17;
            }
            catch { return false; }
        }

        /// <summary>Park on the actor's tile; the standard landing line speaks
        /// with the initiative ordinal as its leading fact. An off-viewport
        /// actor gets an honest refusal receipt instead of a phantom landing —
        /// the cursor does not move.</summary>
        private static void LandOnCharacter(object map, object ch, int ordinal)
        {
            object tile = TileOf(ch);
            if (tile == null) return;
            int tx, ty;
            try
            {
                tx = (int)Seams.MapTile_getTileX.Invoke(tile, null);
                ty = (int)Seams.MapTile_getTileY.Invoke(tile, null);
            }
            catch { return; }
            if (!InParkWindow(map, tx, ty))
            {
                string name = CombatSpine.DisplayNameOf(ch) ?? "Someone";
                Scaffold.SpeechService.Say($"{ordinal}, {name}, out of view.", "Nav");
                return;
            }
            _tileX = tx;
            _tileY = ty;
            _held = true;
            AssertMouse(map);
            QueueLanding(null, $"{ordinal}, ");
        }

        /// <summary>P: center on the active unit — the same meaning P has
        /// overland (recenter on who matters right now). The acting character
        /// during rounds; the PC being placed during deployment.</summary>
        private static void RecenterOnActor(object map)
        {
            ClearTooltip();
            object actor = ActingCharacter();
            object tile = actor == null ? null : TileOf(actor);
            if (tile == null)
            {
                Scaffold.SpeechService.Say("No active unit.", "Nav");
                return;
            }
            int tx, ty;
            try
            {
                tx = (int)Seams.MapTile_getTileX.Invoke(tile, null);
                ty = (int)Seams.MapTile_getTileY.Invoke(tile, null);
            }
            catch { return; }
            if (!InParkWindow(map, tx, ty))
            {
                Scaffold.SpeechService.Say("Active unit out of view.", "Nav");
                return;
            }
            _tileX = tx;
            _tileY = ty;
            _held = true;
            AssertMouse(map);
            QueueLanding(null);
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
            // Layer 1 (§6.18): I/U = the initiative walk, P = center on the
            // active unit (replaces reverse-scan — the rings wrap forward,
            // matching the overland precedent).
            if (Input.GetKeyDown(KeyCode.I)) { StepInitiative(map, +1); return true; }
            if (Input.GetKeyDown(KeyCode.U)) { StepInitiative(map, -1); return true; }
            if (Input.GetKeyDown(KeyCode.P)) { RecenterOnActor(map); return true; }

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
        private static void QueueLanding(string tail, string prefix = null)
        {
            if (_pendingSpeak && Time.frameCount > _pendingFrame)
            {
                _pendingSpeak = false;
                object map = CurrentMap();
                if (map != null && InCombat())
                    SpeakTile(map, _pendingX, _pendingY, _pendingTail, _pendingPrefix);
            }
            _pendingSpeak = true;
            _pendingFrame = Time.frameCount;
            _pendingX = _tileX;
            _pendingY = _tileY;
            _pendingTail = tail;
            _pendingPrefix = prefix;
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
            SpeakTile(map, _pendingX, _pendingY, _pendingTail, _pendingPrefix);
        }

        // ---- The unified landing readout ----

        private static void SpeakTile(object map, int tx, int ty, string countTail, string prefix = null)
        {
            string lead0 = prefix ?? "";
            if (!AnchorPos(out int ax, out int ay)) { ax = tx; ay = ty; }
            object tile = TileAt(map, tx, ty);
            if (tile == null)
            {
                ReviewLayer.ClearStaged();   // no combatant here (Sonnet find 2)
                Scaffold.SpeechService.Say(lead0 + "Nothing." + Offset(tx - ax, ty - ay), "Nav");
                return;
            }

            if (!B(Seams.MapTile_isSpotted, tile))
            {
                ReviewLayer.ClearStaged();   // no combatant here (Sonnet find 2)
                Scaffold.SpeechService.Say(lead0 + "Unexplored." + Offset(tx - ax, ty - ay) + (countTail ?? ""), "Nav");
                return;
            }

            string label = TileLabel(tile);
            string offset = Offset(tx - ax, ty - ay);
            string fact = "";
            string valid = "";

            // Layer 1 (§6.18): a combatant landing carries the ruled
            // top-level payload and stages the drilldown document; any other
            // landing clears the staged document so the plain tile inspect
            // resumes on the reading plane. The gate is the game's OWN
            // inspect gate (isSpotted || isPC — getInspectDescription refuses
            // unspotted units with "too dark", so stats for an
            // illuminated-but-unspotted body render nowhere and stay
            // unspoken; the label ladder still names it). isPC
            // short-circuits ahead of isSpotted (which mutates for PCs —
            // survey hazard).
            object occupant = null;
            try { occupant = Seams.MapTile_getLiveCharacter?.Invoke(tile, null); } catch { }
            bool identifiable = occupant != null
                && (B(Seams.Character_isPC, occupant) || B(Seams.Character_isSpotted, occupant));
            string payload = "";
            if (identifiable)
            {
                payload = CombatantDocument.LandingPayload(occupant);
                var doc = CombatantDocument.Compose(occupant);
                if (doc != null) ReviewLayer.NoteStagedDocument("Combatant", doc);
                else ReviewLayer.ClearStaged();
            }
            else
            {
                ReviewLayer.ClearStaged();
            }

            if (InPlacement())
            {
                bool isValid = IsValidPlacement(map, tile);
                // The perimeter crossing speaks itself (owner ruling).
                string crossing = "";
                if (_lastValidKnown && _lastValid && !isValid) crossing = "Out of bounds. ";
                _lastValidKnown = true;
                _lastValid = isValid;
                valid = isValid ? "Valid" : "Invalid";
                bool lead = _cfgValidPrepend != null && _cfgValidPrepend.Value;
                string body = lead ? valid + ", " + label : label + ", " + valid.ToLowerInvariant();
                Scaffold.SpeechService.Say(
                    lead0 + crossing + body + offset + payload + LightTail(tile) + (countTail ?? ""), "Nav");
                NoteInspect(tile);
                return;
            }

            fact = PathFact(map, tile, tx, ty, ax, ay);
            Scaffold.SpeechService.Say(lead0 + label + offset + fact + payload + LightTail(tile) + (countTail ?? ""), "Nav");
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
                    // The game's hover recompute can be MISSED (its gate
                    // requires an open neighbour via getNextMoveTile — null
                    // when the actor is boxed in — and a hover-tile change
                    // edge; the ride showed path facts vanishing after the
                    // first landing, Opus log review 2026-08-19). Repair with
                    // the game's OWN recompute — the identical findCombatPath
                    // call hover makes — under the game's own actor
                    // conditions, then re-read through the destination guard.
                    if (RequestCourseRecompute(map, actor, tx, ty))
                        len = CourseLength(actor, tx, ty);
                }
                if (len <= 0)
                {
                    // No course terminating at THIS tile = unreachable (a
                    // failed hover recompute leaves the previous course in
                    // place — the destination check is the guard, review
                    // find 1).
                    Scaffold.Log.Debug("Compose",
                        $"path absent ({tx},{ty}) — no course at tile after recompute");
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

        /// <summary>Invoke the game's own combat-path recompute for the
        /// cursor tile — mouse-identity: the exact call CombatEncounter.
        /// updateMousePosition makes on a hover-tile change, under the same
        /// actor conditions (PC and planning established by the caller). The
        /// one game gate deliberately skipped is canMouseMove's open-neighbour
        /// check — a boxed-in actor still deserves a truthful path readout,
        /// and setPath on an unreachable target writes nothing.</summary>
        private static bool RequestCourseRecompute(object map, object actor, int tx, int ty)
        {
            try
            {
                if (Seams.Map_findCombatPath == null || Seams.Character_getTileParty == null) return false;
                if (Seams.Character_canCharacterCombatMove == null
                    || !(bool)Seams.Character_canCharacterCombatMove.Invoke(actor, null)) return false;
                if (Seams.Character_moveAlongCombatPath != null
                    && (bool)Seams.Character_moveAlongCombatPath.GetValue(actor)) return false;
                if (Seams.Character_isPanicked != null
                    && (bool)Seams.Character_isPanicked.Invoke(actor, null)) return false;
                object party = Seams.Character_getTileParty.Invoke(actor, null);
                if (party == null) return false;
                Seams.Map_findCombatPath.Invoke(map, new object[] { party, tx, ty });
                return true;
            }
            catch { return false; }
        }

        // The course destination is System.Drawing.Point (NavigationCourse's
        // own using) — X/Y are PROPERTIES; the old field-first probe made
        // HarmonyX warn twice per landing (Opus log review 2026-08-19).
        // Property-first via plain reflection (never logs), cached per type.
        private static Type _pointType;
        private static System.Reflection.PropertyInfo _pointPX, _pointPY;
        private static System.Reflection.FieldInfo _pointFX, _pointFY;

        private static bool PointMatches(object point, int x, int y)
        {
            try
            {
                var t = point.GetType();
                if (t != _pointType)
                {
                    _pointType = t;
                    _pointPX = t.GetProperty("X") ?? t.GetProperty("x");
                    _pointPY = t.GetProperty("Y") ?? t.GetProperty("y");
                    _pointFX = _pointPX == null ? (t.GetField("X") ?? t.GetField("x")) : null;
                    _pointFY = _pointPY == null ? (t.GetField("Y") ?? t.GetField("y")) : null;
                }
                if (_pointPX != null && _pointPY != null)
                    return Convert.ToInt32(_pointPX.GetValue(point, null)) == x
                        && Convert.ToInt32(_pointPY.GetValue(point, null)) == y;
                if (_pointFX != null && _pointFY != null)
                    return Convert.ToInt32(_pointFX.GetValue(point)) == x
                        && Convert.ToInt32(_pointFY.GetValue(point)) == y;
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
                    bool wanted;
                    switch (cat)
                    {
                        case Ring.Hostiles: wanted = hostile; break;
                        case Ring.NeutralsFriendlies: wanted = !hostile && !isPC; break;
                        case Ring.Party: wanted = isPC; break;
                        default: wanted = !hostile; break;   // Friendlies (the N scan): all non-hostile
                    }
                    if (!wanted) continue;
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

        // (ScanReverse retired 2026-08-21, combat Layer 1: P is now
        // center-on-active-unit, matching overland P; the rings wrap forward.)

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

        // ---- The K tables (Layer 2): the latched four-tab instrument ----

        private static void OpenList(object map)
        {
            ClearTooltip();
            if (!AnchorPos(out int ax, out int ay)) return;
            var hostiles = BuildRing(map, Ring.Hostiles, ax, ay);
            var friendlies = BuildRing(map, Ring.Friendlies, ax, ay);
            if (hostiles.Count == 0 && friendlies.Count == 0 && InitiativeOrder().Count == 0)
            {
                // Nothing to browse: never enter the latch (review find 5 —
                // an empty modal just eats an extra escape press).
                Scaffold.SpeechService.Say("No combatants in view.", "Nav");
                return;
            }
            _listOpen = true;
            _tabIdx = 1;   // Hostiles first (threat-first; wording/tab at calibration)
            for (int i = 0; i < _tabRow.Length; i++) _tabRow[i] = -1;
            if (!_latchGateLogged)
            {
                _latchGateLogged = true;
                Scaffold.Log.Debug("Gate",
                    "combat WASD latch armed — binding swallow + stick force-false (receipt 7)");
            }
            // The census names the ACTUAL tabs (Sonnet find 4 — the old
            // two-way split counted "friendlies" PC-inclusive, matching no
            // tab). Wording at calibration.
            var neutrals = BuildRing(map, Ring.NeutralsFriendlies, ax, ay);
            var party = BuildRing(map, Ring.Party, ax, ay);
            Scaffold.SpeechService.Say(
                $"Hostiles, {hostiles.Count}. Neutrals and friendlies, {neutrals.Count}. Party, {party.Count}. {TabNames[_tabIdx]} tab.",
                "Nav");
        }

        private static string CountPhrase(int n, string singular, string plural = null)
            => n == 1 ? $"1 {singular}" : $"{n} {plural ?? singular + "s"}";

        private static bool ProcessListInput(object map)
        {
            // Z = the native click on the landed tile: the latch closes
            // SILENTLY and the press falls through (the ride-verified
            // discipline; the tables must never survive the action they
            // just launched).
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
            // Cursor-navigation keys supersede the latch (Sonnet find 3
            // class): close silently, fall through to the normal handler.
            if (Input.GetKeyDown(KeyCode.H) || Input.GetKeyDown(KeyCode.N)
                || Input.GetKeyDown(KeyCode.I) || Input.GetKeyDown(KeyCode.U)
                || Input.GetKeyDown(KeyCode.P))
            {
                CloseListSilent();
                return false;
            }
            // Nav revision §5 (owner ruling 2026-08-23): the K instruments
            // are OVERLAYS over a live world, one creature in both worlds —
            // Up/Down = entries, Left/Right = pages (the four tabs here, the
            // categories overland). WASD is RELEASED to native character
            // stepping, exactly like walking overland with the POI list
            // open; the staged document's section walk lives on the review
            // cluster (its proper house), not in the latch.
            if (Input.GetKeyDown(KeyCode.LeftArrow)) { StepTab(-1); return true; }
            if (Input.GetKeyDown(KeyCode.RightArrow)) { StepTab(+1); return true; }
            if (Input.GetKeyDown(KeyCode.DownArrow)) { TabRowStep(map, +1); return true; }
            if (Input.GetKeyDown(KeyCode.UpArrow)) { TabRowStep(map, -1); return true; }
            return false;
        }

        private static void StepTab(int dir)
        {
            _tabIdx = ((_tabIdx + dir) % TabNames.Length + TabNames.Length) % TabNames.Length;
            int count = _tabIdx == 0 ? InitiativeOrder().Count : TabRing()?.Count ?? 0;
            Scaffold.SpeechService.Say($"{TabNames[_tabIdx]}, {count}.", "Nav");
        }

        private static List<Entry> TabRing()
        {
            object map = CurrentMap();
            if (map == null || !AnchorPos(out int ax, out int ay)) return null;
            Ring cat = _tabIdx == 1 ? Ring.Hostiles
                : _tabIdx == 2 ? Ring.NeutralsFriendlies : Ring.Party;
            return BuildRing(map, cat, ax, ay);
        }

        /// <summary>Up/Down inside the latch: rows rebuilt live per press;
        /// the per-tab cursor clamps at the edges. Turn Order rows carry
        /// ordinal + turn-econ status with geography suppressed (owner
        /// ruling); spatial rows are the standard landing with the trailing
        /// counter.</summary>
        private static void TabRowStep(object map, int dir)
        {
            if (_tabIdx == 0)
            {
                var order = InitiativeOrder();
                if (order.Count == 0)
                {
                    Scaffold.SpeechService.Say("Turn Order, none.", "Nav");
                    return;
                }
                _tabRow[0] = _tabRow[0] < 0
                    ? (dir > 0 ? 0 : order.Count - 1)
                    : Mathf.Clamp(_tabRow[0] + dir, 0, order.Count - 1);
                TurnOrderRow(map, order[_tabRow[0]], _tabRow[0] + 1);
                return;
            }
            var ring = TabRing();
            if (ring == null || ring.Count == 0)
            {
                Scaffold.SpeechService.Say($"{TabNames[_tabIdx]}, none.", "Nav");
                return;
            }
            _tabRow[_tabIdx] = _tabRow[_tabIdx] < 0
                ? (dir > 0 ? 0 : ring.Count - 1)
                : Mathf.Clamp(_tabRow[_tabIdx] + dir, 0, ring.Count - 1);
            LandOn(map, ring[_tabRow[_tabIdx]], _tabRow[_tabIdx], ring.Count);
        }

        /// <summary>The Turn Order row: ordinal + name + the game's own
        /// initiative-status word, then the slim payload — geography
        /// suppressed (turn-econ replaces it, owner ruling). The park still
        /// happens (Z acts on the row); no deferral — the row carries no
        /// path fact, and the payload reads the character directly.</summary>
        private static void TurnOrderRow(object map, object ch, int ordinal)
        {
            ClearTooltip();
            object tile = TileOf(ch);
            if (tile == null) return;
            int tx, ty;
            try
            {
                tx = (int)Seams.MapTile_getTileX.Invoke(tile, null);
                ty = (int)Seams.MapTile_getTileY.Invoke(tile, null);
            }
            catch { return; }
            string name = CombatSpine.DisplayNameOf(ch) ?? "Someone";
            string status = "";
            try
            {
                string raw = Seams.Character_printInitiativeStatus?.Invoke(ch, null) as string;
                if (!string.IsNullOrWhiteSpace(raw))
                    status = ", " + Patches.TextCleaner.CleanText(raw).Trim().ToLowerInvariant();
            }
            catch { }

            bool identifiable = B(Seams.Character_isPC, ch) || B(Seams.Character_isSpotted, ch);
            string payload = "";
            if (identifiable)
            {
                payload = CombatantDocument.LandingPayload(ch);
                var doc = CombatantDocument.Compose(ch);
                if (doc != null) ReviewLayer.NoteStagedDocument("Combatant", doc);
                else ReviewLayer.ClearStaged();
            }
            else ReviewLayer.ClearStaged();

            // This row speaks itself — cancel any armed deferred landing in
            // BOTH branches (Sonnet find 3: the off-viewport path left a
            // stale spatial landing to fire right after the refusal).
            _pendingSpeak = false;

            if (InParkWindow(map, tx, ty))
            {
                _tileX = tx;
                _tileY = ty;
                _held = true;
                AssertMouse(map);
                Scaffold.SpeechService.Say($"{ordinal}, {name}{status}.{payload}", "Nav");
            }
            else
            {
                // Off-viewport actor: honest row without a park (the phantom
                // landing hazard, receipt-fixed in Layer 1).
                Scaffold.SpeechService.Say($"{ordinal}, {name}{status}, out of view.{payload}", "Nav");
            }
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
        }
    }
}

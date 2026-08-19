using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SkaldAccessibility
{
    /// <summary>
    /// WP11: the overland cursor — mouse-identity browsing, category scans,
    /// and the catalog list (owner-designed spec, build-plan §WP11; ground
    /// truth in docs\overland-survey.md; adversarially reviewed 2026-08-16).
    ///
    /// The cursor IS the game's virtual mouse: the mod holds a tile and
    /// RE-ASSERTS the virtual mouse onto it every frame (the latch — physical
    /// mouse jitter zeroes the offsets every tick, popups snap the mouse to
    /// buttons, a plugged-in controller drifts it via the right stick; one
    /// latch neutralizes all three). Z and X keep their native meanings:
    /// Z = left click (path / attack / interact on arrival), X = right click
    /// (inspect tooltip, which speaks through the existing Tooltip hook).
    /// WASD party movement is never captured.
    ///
    /// Visibility contract (owner-ruled): speak a tile iff isSpotted() —
    /// the game's own spoiler gate; identify an occupant iff the tile is
    /// illuminated or the character is spotted, else "something unseen";
    /// spotted-but-not-in-sight carries a trailing "out of view" (in-sight =
    /// Euclidean ≤ viewDistance from the party tile + the game's own
    /// testLineOfSight — overland the party occupies one tile).
    ///
    /// Composition is lazy at keypress; nothing is cached but two ints and
    /// the list rings. All game reads are the side-effect-safe set
    /// (never setSpotted, never getAccessibleTilesFromParty, never getParty).
    ///
    /// The POI list (owner redesign 2026-08-19): persistent and DYNAMIC.
    /// It survives interactions; it suspends (never dies) when a popup or a
    /// non-overland state takes over, announcing every on/off edge ("POI
    /// list active." / "POI list closed."), and resumes on return to
    /// overland with rings rebuilt from live truth and the selection
    /// re-anchored by identity. Combat is the one force-close (deployment
    /// needs the free mouse). Rings rebuild on the game's own mutation
    /// clock — the step commit plus player action keys — never on a timer.
    /// Ring membership mirrors the renderer: the removed-prop gate
    /// (shouldBeRemovedFromGame, the picked-up-item fix), hidden, undrawn.
    /// Unlocked empty containers carry ", empty" and sink to the ring tail.
    /// P re-anchors the cursor on the party tile from any overland context.
    /// </summary>
    public static class OverlandCursor
    {
        private enum Category { Hostiles, Neutrals, Loot, Objects, Exits }
        private static readonly string[] CategoryNames = { "hostiles", "neutrals", "loot", "objects", "exits" };

        private struct Entry
        {
            public int X, Y;
            public string Label;
            public object Tracked;   // Character instance for People rings (beacon follows it)
            public int Dist;
            public bool Deprioritized;   // empty containers: ring tail regardless of distance
        }

        private static ConfigEntry<bool> _cfgCloseOnUse;

        internal static void BindConfig(ConfigFile config)
        {
            _cfgCloseOnUse = config.Bind("Overland", "POIListCloseOnUse", false,
                "Close the POI list every time Z activates an entry (default: the list stays open and "
                + "suspends/resumes around whatever the interaction opens).");
        }

        // ---- Cursor state (two ints + a held flag; the latch re-asserts) ----
        private static bool _held;
        private static int _tileX, _tileY;
        private static object _lastMap;

        /// <summary>The WP11 latch owns the virtual mouse while the cursor is
        /// held — the general mouse guard defers to it.</summary>
        internal static bool HoldsMouse => _held;

        // ---- List state (the POI list: persistent, dynamic) ----
        private static bool _listOpen;
        private static List<Entry>[] _rings;    // rebuilt on the step/action clock
        private static int _listCat;
        private static int _listIdx = -1;
        private static int _lastPartyX = int.MinValue, _lastPartyY = int.MinValue;
        private static int _swallowTailFrame = -1;
        private static int _rebuildFrame = -1;      // action keys schedule a next-frame rebuild
        private static bool _announcedActive;       // the on/off edge announcer's memory
        private static int _activeFrame = -1;       // frame-stamped modality cache (predicates
        private static bool _activeCache;           // may run before Tick in a frame)

        public static bool ListOpen => _listOpen;

        /// <summary>The list is open AND owns input right now — overland, no
        /// popup over it. While suspended, every key predicate stands down so
        /// popups and other states keep their native input.</summary>
        private static bool ActiveNow()
        {
            if (Time.frameCount != _activeFrame)
            {
                _activeFrame = Time.frameCount;
                _activeCache = _listOpen && InOverland() && CurrentMap() != null && !PopupUp();
            }
            return _activeCache;
        }

        /// <summary>Choke-point predicate (extends the review swallow): keys
        /// the game must not see while the list is ACTIVE (or in the closing
        /// press's tail). Arrows browse the list; Esc/Backspace close it.
        /// A suspended list swallows nothing — the popup or state over it
        /// owns those keys natively.</summary>
        public static bool ShouldSwallowKey(KeyCode key)
        {
            if (!(Time.frameCount <= _swallowTailFrame || (_listOpen && ActiveNow()))) return false;
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

        /// <summary>Backspace must not fire the emulated B button while the
        /// list is ACTIVE (consulted by ControllerFeedPatch). Z and X stay
        /// LIVE — native click semantics are the point. A suspended list
        /// yields B back to the popup over it.</summary>
        public static bool SuppressButtonB()
            => (_listOpen && ActiveNow()) || Time.frameCount <= _swallowTailFrame;

        // ---- Game-truth reads ----

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

        private static bool InOverland()
        {
            object state = Pump.CurrentStateObject();
            return state != null && Seams.OverlandStateType != null
                && Seams.OverlandStateType.IsInstanceOfType(state);
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

        private static bool PartyPos(object map, out int x, out int y)
        {
            x = 0; y = 0;
            try
            {
                if (map == null || Seams.Map_getXPos == null || Seams.Map_getYPos == null) return false;
                x = (int)Seams.Map_getXPos.Invoke(map, null);
                y = (int)Seams.Map_getYPos.Invoke(map, null);
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

        // ---- The latch ----

        /// <summary>Called from Plugin.Update every frame in EVERY state (A3:
        /// must stay in Update so a same-frame Z clicks the new tile).
        /// Runs the POI list's modality edges (suspend/resume announcing),
        /// the dynamic ring rebuilds on the step/action clock, the beacon,
        /// and the mouse latch; resets on map change.</summary>
        public static void Tick()
        {
            try
            {
                bool inOverland = InOverland();
                object map = inOverland ? CurrentMap() : null;

                if (map != null && !ReferenceEquals(map, _lastMap))
                {
                    _lastMap = map;
                    Drop();       // new map: cursor and list reset (the edge below announces)
                }

                // ---- Modality edges (run in every state) ----
                bool active = _listOpen && map != null && !PopupUp();
                _activeFrame = Time.frameCount;
                _activeCache = active;
                if (active && !_announcedActive)
                {
                    // Resume from an excursion: fresh truth, silent re-anchor,
                    // one announcement. (Explicit K opens announce for
                    // themselves and pre-set the flag — they never land here.)
                    if (PartyPos(map, out int rx, out int ry))
                    {
                        RebuildRings(map, rx, ry);
                        _lastPartyX = rx; _lastPartyY = ry;
                    }
                    _announcedActive = true;
                    Scaffold.SpeechService.SayQueued("POI list active.", "Nav");
                }
                else if (!active && _announcedActive)
                {
                    _announcedActive = false;
                    Scaffold.SpeechService.SayQueued("POI list closed.", "Nav");
                }

                if (map == null) return;
                if (PopupUp() || Patches.GridNavigationPatch.GridActive()) return;

                // ---- Dynamic rebuild + beacon (list open, overland, unobstructed) ----
                if (_listOpen && PartyPos(map, out int px, out int py))
                {
                    // Action keys schedule a next-frame rebuild: the world only
                    // mutates on player actions (step commits, bump interacts,
                    // clicks) — there is nothing to poll between them.
                    if (Input.GetKeyDown(KeyCode.Z) || Input.GetKeyDown(KeyCode.X)
                        || Input.GetKeyDown(KeyCode.Space)
                        || Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.A)
                        || Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.D))
                        _rebuildFrame = Time.frameCount + 1;

                    bool moved = (_lastPartyX != px || _lastPartyY != py) && _lastPartyX != int.MinValue;
                    if (moved || Time.frameCount == _rebuildFrame)
                    {
                        RebuildRings(map, px, py);
                        // Beacon: party stepped while an entity is landed.
                        if (moved && _listIdx >= 0) SpeakBeacon(map, px, py);
                    }
                    _lastPartyX = px; _lastPartyY = py;
                }

                if (!_held) return;
                AssertMouse(map);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[Cursor] {ex.Message}");
            }
        }

        /// <summary>Rebuild all five rings from live map truth and re-anchor
        /// the selection by identity — tracked reference first, tile
        /// coordinates second (props are tile-fixed; labels may legitimately
        /// change, e.g. a chest gaining ", empty"). A vanished selection goes
        /// to -1 (unbrowsed): the next arrow starts from the top. Silent by
        /// design — additions and removals never speak (noise ruling).</summary>
        private static void RebuildRings(object map, int px, int py)
        {
            if (_rings == null) return;
            object trackedSel = null;
            int selX = 0, selY = 0;
            bool hadSel = _listIdx >= 0 && _listIdx < _rings[_listCat].Count;
            if (hadSel)
            {
                var s = _rings[_listCat][_listIdx];
                trackedSel = s.Tracked;
                selX = s.X; selY = s.Y;
            }

            for (int i = 0; i < 5; i++)
                _rings[i] = BuildRing(map, (Category)i, px, py);

            if (!hadSel)
            {
                if (_listIdx >= _rings[_listCat].Count) _listIdx = -1;
                return;
            }
            var ring = _rings[_listCat];
            int found = trackedSel != null
                ? ring.FindIndex(e => ReferenceEquals(e.Tracked, trackedSel))
                : ring.FindIndex(e => e.Tracked == null && e.X == selX && e.Y == selY);
            _listIdx = found;
        }

        /// <summary>Compute the held tile's virtual-screen center and set the
        /// virtual mouse there. Formula from the adversarial review (A9):
        /// col c = tx - viewportX + 12 (1..23 visible), mx = (12 - scrollX)
        /// + 16c + 8; row r = ty - viewportY + 9, my = -scrollY + 16r + 8.
        /// Tile centers are always expressible; scroll offsets are read live
        /// so mid-slide frames stay accurate.</summary>
        private static void AssertMouse(object map)
        {
            if (Seams.SkaldIO_setVirtualMousePosition == null
                || Seams.Map_getViewportX == null || Seams.Map_getViewportY == null) return;

            int vx = (int)Seams.Map_getViewportX.Invoke(map, null);
            int vy = (int)Seams.Map_getViewportY.Invoke(map, null);

            int c = _tileX - vx + 12;
            int r = _tileY - vy + 9;
            if (c < 1 || c > 23 || r < 1 || r > 17) return; // scrolled out of view; latch resumes when visible

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
            Seams.SkaldIO_setVirtualMousePosition.Invoke(null, new object[] { mx, my });
        }

        private static void Drop()
        {
            _held = false;
            CloseListSilent();
        }

        /// <summary>From the state clock. The cursor hold never survives a
        /// transition (the latch must not fight the new state's mouse). The
        /// POI list survives SUSPENDED through non-combat excursions
        /// (dialogue, sheets, menus) and resumes when overland returns;
        /// combat force-closes it — deployment needs the free mouse (owner
        /// ruling 2026-08-19). Tick's edge announcer speaks both cases.</summary>
        public static void OnStateTransition()
        {
            _held = false;
            try
            {
                object state = Pump.CurrentStateObject();
                if (state != null && state.GetType().Name.StartsWith("Combat"))
                    CloseListSilent();
            }
            catch { }
        }

        // ---- Input (called from InputHandler after the review layer) ----

        /// <summary>Returns true when the press was the cursor's.</summary>
        public static bool ProcessInput()
        {
            if (!InOverland()) return false;
            object map = CurrentMap();
            if (map == null || PopupUp() || Patches.GridNavigationPatch.GridActive()) return false;

            // The catalog list owns arrows and its exits while open.
            if (_listOpen && ProcessListInput(map)) return true;

            if (Input.GetKeyDown(KeyCode.K)) { OpenList(map); return true; }

            if (!_listOpen)
            {
                if (Input.GetKeyDown(KeyCode.UpArrow)) { Nudge(map, 0, 1); return true; }
                if (Input.GetKeyDown(KeyCode.DownArrow)) { Nudge(map, 0, -1); return true; }
                if (Input.GetKeyDown(KeyCode.LeftArrow)) { Nudge(map, -1, 0); return true; }
                if (Input.GetKeyDown(KeyCode.RightArrow)) { Nudge(map, 1, 0); return true; }
            }

            if (Input.GetKeyDown(KeyCode.H)) { Scan(map, Category.Hostiles); return true; }
            if (Input.GetKeyDown(KeyCode.N)) { Scan(map, Category.Neutrals); return true; }
            if (Input.GetKeyDown(KeyCode.B)) { Scan(map, Category.Loot); return true; }
            if (Input.GetKeyDown(KeyCode.O)) { Scan(map, Category.Objects); return true; }
            if (Input.GetKeyDown(KeyCode.M)) { Scan(map, Category.Exits); return true; }
            if (Input.GetKeyDown(KeyCode.P)) { Recenter(map); return true; }

            return false;
        }

        /// <summary>P: re-anchor the cursor on the party tile from any
        /// overland context (owner ruling 2026-08-19, replacing the reverse
        /// scan). Works with the list open — the cursor moves, the list and
        /// its selection stay; the next arrow resumes browsing.</summary>
        private static void Recenter(object map)
        {
            ClearTooltip();
            if (!PartyPos(map, out int px, out int py)) return;
            _held = true;
            _tileX = px;
            _tileY = py;
            AssertMouse(map);
            SpeakTile(map, px, py, px, py, null);
        }

        // ---- Nudge ----

        private static void Nudge(object map, int dx, int dy)
        {
            ClearTooltip();
            if (!PartyPos(map, out int px, out int py)) return;

            if (!_held)
            {
                // First use (or after a drop): anchor on the party tile.
                _held = true;
                _tileX = px;
                _tileY = py;
            }
            else
            {
                int nx = _tileX + dx, ny = _tileY + dy;
                // Clamp to the visible 23x17 window — the mouse cannot leave it.
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
            SpeakTile(map, _tileX, _tileY, px, py, null);
        }

        // ---- Composition ----

        private static void SpeakTile(object map, int tx, int ty, int px, int py, string countTail)
        {
            object tile = TileAt(map, tx, ty);
            if (tile == null)
            {
                Scaffold.SpeechService.Say("Nothing." + Offset(tx - px, ty - py), "Nav");
                return;
            }

            if (!B(Seams.MapTile_isSpotted, tile))
            {
                Scaffold.SpeechService.Say("Unexplored." + Offset(tx - px, ty - py) + (countTail ?? ""), "Nav");
                return;
            }

            string label = TileLabel(map, tile, tx, ty, px, py);
            string qualifier = InSight(map, tx, ty, px, py) ? "" : ", out of view";
            Scaffold.SpeechService.Say(label + qualifier + Offset(tx - px, ty - py) + (countTail ?? ""), "Nav");

            // The game's own inspect text is the review panel (raw — tag
            // grammar sections it). Only spotted tiles produce one.
            string inspect = S(Seams.MapTile_getInspectDescription, tile);
            if (!string.IsNullOrWhiteSpace(inspect)) ReviewLayer.NotePanel("Inspect", inspect);
        }

        /// <summary>Rendered-priority content label for a spotted tile.</summary>
        private static string TileLabel(object map, object tile, int tx, int ty, int px, int py)
        {
            // Live occupant, per the occupant contract.
            object ch = null;
            try { ch = Seams.MapTile_getLiveCharacter?.Invoke(tile, null); } catch { }
            if (ch != null)
            {
                if (B(Seams.Character_isPC, ch)) return "Party";
                bool identifiable = B(Seams.MapTile_isIlluminated, tile) || B(Seams.Character_isSpotted, ch);
                if (identifiable)
                {
                    string name = Patches.TextCleaner.CleanText(S(Seams.SkaldBaseObject_getName, ch) ?? "Someone");
                    return B(Seams.Character_isHostile, ch) ? name + ", hostile" : name;
                }
                if (!B(Seams.MapTile_isConcealment, tile)) return "Something unseen";
            }

            if (B(Seams.MapTile_hasVehicle, tile))
            {
                object v = null;
                try { v = Seams.MapTile_getVehicle?.Invoke(tile, null); } catch { }
                string vn = v != null ? Patches.TextCleaner.CleanText(S(Seams.SkaldBaseObject_getName, v) ?? "") : "";
                return string.IsNullOrWhiteSpace(vn) ? "Ship" : vn;
            }

            object prop = PropAt(tile);
            if (prop != null)
            {
                string pn = Patches.TextCleaner.CleanText(S(Seams.SkaldBaseObject_getName, prop) ?? "");
                string verb = S(Seams.MapTile_getVerb, tile);
                if (!string.IsNullOrWhiteSpace(pn))
                {
                    string label = string.IsNullOrWhiteSpace(verb) ? pn : pn + ", " + Patches.TextCleaner.CleanText(verb);
                    if (IsEmptyContainer(tile, prop)) label += ", empty";
                    return label;
                }
            }

            if (GroundItems(tile)) return "Items";

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

        private static bool GroundItems(object tile)
        {
            // Mirrors the renderer's ground-item icon gate: non-empty inventory
            // on a passable tile (the illustrator runs this same read per frame).
            try
            {
                if (!B(Seams.MapTile_isPassable, tile)) return false;
                object inv = Seams.MapTile_getInventory?.Invoke(tile, null);
                return inv != null && Seams.Inventory_isEmpty != null && !(bool)Seams.Inventory_isEmpty.Invoke(inv, null);
            }
            catch { return false; }
        }

        /// <summary>"4 north, 2 east" trailing; "here" on the party tile.
        /// World y increases north (survey §1).</summary>
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

        /// <summary>"In sight now" per the game's own viewshed rule (A8):
        /// Euclidean ≤ viewDistance from the party tile plus the game's
        /// testLineOfSight. Overland the whole party occupies one tile.</summary>
        private static bool InSight(object map, int tx, int ty, int px, int py)
        {
            try
            {
                float viewDist = 5.99f;
                if (Seams.Map_viewDistance != null)
                    viewDist = (float)Seams.Map_viewDistance.GetValue(map);
                int dx = tx - px, dy = ty - py;
                if (Math.Sqrt(dx * dx + dy * dy) > viewDist) return false;
                if (Seams.Map_tileGrid == null || Seams.MapTileGrid_testLineOfSight == null) return true;
                object grid = Seams.Map_tileGrid.GetValue(map);
                if (grid == null) return true;
                return (bool)Seams.MapTileGrid_testLineOfSight.Invoke(grid, new object[] { px, py, tx, ty });
            }
            catch { return true; }
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

        // ---- Category rings ----

        private static List<Entry> BuildRing(object map, Category cat, int px, int py)
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

                    int dist = Math.Abs(tx - px) + Math.Abs(ty - py);
                    switch (cat)
                    {
                        case Category.Hostiles:
                        case Category.Neutrals:
                        {
                            object ch = null;
                            try { ch = Seams.MapTile_getLiveCharacter?.Invoke(tile, null); } catch { }
                            if (ch == null || B(Seams.Character_isPC, ch)) break;
                            bool identifiable = B(Seams.MapTile_isIlluminated, tile) || B(Seams.Character_isSpotted, ch);
                            if (!identifiable)
                            {
                                // Unseen-icon state: tail of the Hostiles ring (caution bias).
                                if (cat == Category.Hostiles && !B(Seams.MapTile_isConcealment, tile))
                                    unseenTail.Add(new Entry { X = tx, Y = ty, Label = "Something unseen", Tracked = ch, Dist = dist });
                                break;
                            }
                            bool hostile = B(Seams.Character_isHostile, ch);
                            if ((cat == Category.Hostiles) != hostile) break;
                            string name = Patches.TextCleaner.CleanText(S(Seams.SkaldBaseObject_getName, ch) ?? "Someone");
                            ring.Add(new Entry { X = tx, Y = ty, Label = name, Tracked = ch, Dist = dist });
                            break;
                        }
                        case Category.Loot:
                        {
                            object prop = PropAt(tile);
                            if (prop != null && (IsType(prop, Seams.PropContType) || IsType(prop, Seams.PropPickupType)))
                            {
                                var pe = PropEntry(tile, prop, tx, ty, dist);
                                if (IsEmptyContainer(tile, prop))
                                {
                                    // Pure QoL (owner ruling 2026-08-19): looted
                                    // containers are labeled and sink to the tail.
                                    pe.Label += ", empty";
                                    pe.Deprioritized = true;
                                }
                                ring.Add(pe);
                            }
                            else if (GroundItems(tile))
                                ring.Add(new Entry { X = tx, Y = ty, Label = "Items", Dist = dist });
                            break;
                        }
                        case Category.Objects:
                        {
                            object prop = PropAt(tile);
                            if (prop == null) break;
                            if (IsType(prop, Seams.PropWarpType) || IsType(prop, Seams.PropContType)
                                || IsType(prop, Seams.PropPickupType) || IsType(prop, Seams.PropBeaconType)
                                || IsType(prop, Seams.PropSpawnerType) || IsType(prop, Seams.PropTriggerType)) break;
                            string pn = Patches.TextCleaner.CleanText(S(Seams.SkaldBaseObject_getName, prop) ?? "");
                            if (IsType(prop, Seams.PropDecorativeType) && string.IsNullOrWhiteSpace(pn)) break;
                            if (string.IsNullOrWhiteSpace(pn)) break;
                            ring.Add(PropEntry(tile, prop, tx, ty, dist));
                            break;
                        }
                        case Category.Exits:
                        {
                            object prop = PropAt(tile);
                            if (prop != null && IsType(prop, Seams.PropWarpType))
                            {
                                ring.Add(PropEntry(tile, prop, tx, ty, dist));
                                break;
                            }
                            string nested = S(Seams.MapTile_getNestedMapId, tile);
                            if (!string.IsNullOrEmpty(nested))
                            {
                                string pn = prop != null ? Patches.TextCleaner.CleanText(S(Seams.SkaldBaseObject_getName, prop) ?? "") : "";
                                ring.Add(new Entry { X = tx, Y = ty, Label = string.IsNullOrWhiteSpace(pn) ? "Entrance" : pn, Dist = dist });
                                break;
                            }
                            if (B(Seams.MapTile_hasVehicle, tile))
                            {
                                ring.Add(new Entry { X = tx, Y = ty, Label = "Ship", Dist = dist });
                            }
                            break;
                        }
                    }
                }
            }

            if (cat == Category.Exits) AddEdgeExits(map, ring, vx, vy, px, py);
            ring.Sort((a, b) => a.Deprioritized != b.Deprioritized ? (a.Deprioritized ? 1 : -1)
                : a.Dist != b.Dist ? a.Dist - b.Dist : (a.Y != b.Y ? b.Y - a.Y : a.X - b.X));
            if (unseenTail.Count > 0)
            {
                unseenTail.Sort((a, b) => a.Dist - b.Dist);
                ring.AddRange(unseenTail);
            }
            return ring;
        }

        /// <summary>One synthetic entry per map-edge direction whose edge id
        /// is set, at the nearest passable border tile visible in the window.</summary>
        private static void AddEdgeExits(object map, List<Entry> ring, int vx, int vy, int px, int py)
        {
            try
            {
                AddEdge(map, ring, Seams.Map_northernEdgeMapId, "North edge", vx, vy, px, py, north: true, south: false, east: false, west: false);
                AddEdge(map, ring, Seams.Map_southernEdgeMapId, "South edge", vx, vy, px, py, north: false, south: true, east: false, west: false);
                AddEdge(map, ring, Seams.Map_easternEdgeMapId, "East edge", vx, vy, px, py, north: false, south: false, east: true, west: false);
                AddEdge(map, ring, Seams.Map_westernEdgeMapId, "West edge", vx, vy, px, py, north: false, south: false, east: false, west: true);
            }
            catch { }
        }

        private static void AddEdge(object map, List<Entry> ring, System.Reflection.FieldInfo idField,
            string label, int vx, int vy, int px, int py, bool north, bool south, bool east, bool west)
        {
            if (idField == null) return;
            if (string.IsNullOrEmpty(idField.GetValue(map) as string)) return;

            Entry best = default;
            bool found = false;
            for (int tx = vx - 11; tx <= vx + 11; tx++)
            {
                for (int ty = vy - 8; ty <= vy + 8; ty++)
                {
                    object tile = TileAt(map, tx, ty);
                    if (tile == null) continue;
                    int mtx = (int)Seams.MapTile_getTileX.Invoke(tile, null);
                    int mty = (int)Seams.MapTile_getTileY.Invoke(tile, null);
                    bool onEdge = (west && mtx == 0) || (south && mty == 0)
                        || (east && !(bool)Seams.Map_isTileValid.Invoke(map, new object[] { mtx + 1, mty }))
                        || (north && !(bool)Seams.Map_isTileValid.Invoke(map, new object[] { mtx, mty + 1 }));
                    if (!onEdge || !B(Seams.MapTile_isPassable, tile)) continue;
                    int dist = Math.Abs(tx - px) + Math.Abs(ty - py);
                    if (!found || dist < best.Dist)
                    {
                        best = new Entry { X = tx, Y = ty, Label = label, Dist = dist };
                        found = true;
                    }
                }
            }
            if (found) ring.Add(best);
        }

        /// <summary>The renderer's full prop gate (MapIllustrator.drawProps):
        /// removed-from-game (a picked-up PropPickup stays referenced by its
        /// tile forever — only this flag marks it dead), hidden, undrawn.</summary>
        private static object PropAt(object tile)
        {
            try
            {
                object prop = Seams.MapTile_getPropOrGuestProp?.Invoke(tile, null);
                if (prop == null) return null;
                if (B(Seams.Prop_shouldBeRemovedFromGame, prop)) return null;
                if (B(Seams.Prop_isHidden, prop) || B(Seams.Prop_shouldNotBeDrawn, prop)) return null;
                return prop;
            }
            catch { return null; }
        }

        /// <summary>An unlocked container with nothing left to take. Loot
        /// loadouts land in the TILE inventory at prop placement, so
        /// emptiness is authoritative from map creation. Locked containers
        /// never report empty — the lock hides the contents from sighted
        /// players too (owner ruling 2026-08-19).</summary>
        private static bool IsEmptyContainer(object tile, object prop)
        {
            try
            {
                if (!IsType(prop, Seams.PropContType)) return false;
                if (B(Seams.PropLockable_isLocked, prop)) return false;
                object inv = Seams.MapTile_getInventory?.Invoke(tile, null);
                return inv != null && Seams.Inventory_isEmpty != null
                    && (bool)Seams.Inventory_isEmpty.Invoke(inv, null);
            }
            catch { return false; }
        }

        private static Entry PropEntry(object tile, object prop, int tx, int ty, int dist)
        {
            string pn = Patches.TextCleaner.CleanText(S(Seams.SkaldBaseObject_getName, prop) ?? "");
            return new Entry { X = tx, Y = ty, Label = string.IsNullOrWhiteSpace(pn) ? "Object" : pn, Dist = dist };
        }

        private static bool IsType(object o, Type t) => t != null && t.IsInstanceOfType(o);

        // ---- Scans (stateless: the cursor position is the ring position) ----

        private static void Scan(object map, Category cat)
        {
            ClearTooltip();
            if (!PartyPos(map, out int px, out int py)) return;
            var ring = BuildRing(map, cat, px, py);
            if (ring.Count == 0)
            {
                Scaffold.SpeechService.Say($"No {CategoryNames[(int)cat]}.", "Nav");
                return;
            }
            int at = _held ? ring.FindIndex(e => e.X == _tileX && e.Y == _tileY) : -1;
            int next = at < 0 ? 0 : (at + 1) % ring.Count;
            JumpTo(map, ring[next], next, ring.Count, px, py);
        }

        private static void JumpTo(object map, Entry e, int index, int count, int px, int py)
        {
            _held = true;
            _tileX = e.X;
            _tileY = e.Y;
            AssertMouse(map);
            SpeakTile(map, e.X, e.Y, px, py, count > 1 ? $" {index + 1} of {count}." : null);
        }

        // ---- The catalog list ----

        private static void OpenList(object map)
        {
            if (ReviewLayer.Active) ReviewLayer.CloseSilent();
            ClearTooltip();
            if (!PartyPos(map, out int px, out int py)) return;

            _rings = new List<Entry>[5];
            var census = new List<string>();
            int unseen = 0;
            for (int i = 0; i < 5; i++)
            {
                _rings[i] = BuildRing(map, (Category)i, px, py);
                int n = _rings[i].Count;
                if (i == (int)Category.Hostiles)
                {
                    unseen = _rings[i].FindAll(e => e.Label == "Something unseen").Count;
                    n -= unseen;
                }
                if (n > 0) census.Add($"{n} {(n == 1 ? CategoryNames[i].TrimEnd('s') : CategoryNames[i])}");
            }
            if (unseen > 0) census.Add($"{unseen} unseen");

            if (census.Count == 0)
            {
                Scaffold.SpeechService.Say("Nothing nearby.", "Nav");
                _rings = null;
                return;
            }

            _listOpen = true;
            _listIdx = -1;
            _listCat = 0;
            while (_listCat < 4 && _rings[_listCat].Count == 0) _listCat++;
            _lastPartyX = px; _lastPartyY = py;
            _announcedActive = true;   // the explicit open IS the on edge
            Scaffold.SpeechService.Say("POI list active. " + string.Join(", ", census.ToArray()) + ".", "Nav");
        }

        private static bool ProcessListInput(object map)
        {
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace)
                || Input.GetKeyDown(KeyCode.K))
            {
                CloseList(announce: true);
                return true;
            }

            // Z: let the NATIVE click fire on the landed tile (the mouse is
            // there by construction — no eat, by design). The list STAYS OPEN
            // (owner redesign 2026-08-19): if the click mounts a popup or a
            // state, the modality edge suspends it and resumes it after;
            // otherwise a next-frame rebuild absorbs whatever the click
            // changed. POIListCloseOnUse restores the old close-per-use.
            if (Input.GetKeyDown(KeyCode.Z))
            {
                ClearTooltip();
                if (_cfgCloseOnUse != null && _cfgCloseOnUse.Value) CloseList(announce: true);
                else _rebuildFrame = Time.frameCount + 1;
                return false;
            }

            if (Input.GetKeyDown(KeyCode.DownArrow)) { ListStep(map, +1); return true; }
            if (Input.GetKeyDown(KeyCode.UpArrow)) { ListStep(map, -1); return true; }
            if (Input.GetKeyDown(KeyCode.RightArrow)) { ListCat(map, +1); return true; }
            if (Input.GetKeyDown(KeyCode.LeftArrow)) { ListCat(map, -1); return true; }

            return false;
        }

        private static void ListStep(object map, int dir)
        {
            var ring = _rings[_listCat];
            if (ring.Count == 0) { Scaffold.SpeechService.Say("Empty.", "Nav"); return; }
            int next = _listIdx < 0 ? (dir > 0 ? 0 : ring.Count - 1) : _listIdx + dir;
            if (next < 0) { Scaffold.SpeechService.Say("Start of list.", "Nav"); return; }
            if (next >= ring.Count) { Scaffold.SpeechService.Say("End of list.", "Nav"); return; }
            _listIdx = next;
            Land(map, prefix: null);
        }

        private static void ListCat(object map, int dir)
        {
            int start = _listCat;
            do
            {
                _listCat = (_listCat + dir + 5) % 5;
            } while (_rings[_listCat].Count == 0 && _listCat != start);
            _listIdx = _rings[_listCat].Count > 0 ? 0 : -1;
            string catName = char.ToUpper(CategoryNames[_listCat][0]) + CategoryNames[_listCat].Substring(1);
            if (_listIdx < 0) { Scaffold.SpeechService.Say($"{catName}: none.", "Nav"); return; }
            Land(map, prefix: $"{catName}: ");
        }

        /// <summary>Land on the current list entry: zip the mouse, speak,
        /// panel. Entities re-validate at speech time.</summary>
        private static void Land(object map, string prefix)
        {
            var e = _rings[_listCat][_listIdx];
            if (!PartyPos(map, out int px, out int py)) return;

            // Tracked characters may have moved or died since the freeze.
            if (e.Tracked != null)
            {
                try
                {
                    if (Seams.MapTile_getLiveCharacter != null)
                    {
                        int cx = (int)Seams.SkaldWorldObject_getTileX.Invoke(e.Tracked, null);
                        int cy = (int)Seams.SkaldWorldObject_getTileY.Invoke(e.Tracked, null);
                        object tile = TileAt(map, cx, cy);
                        object live = tile != null ? Seams.MapTile_getLiveCharacter.Invoke(tile, null) : null;
                        if (!ReferenceEquals(live, e.Tracked))
                        {
                            Scaffold.SpeechService.Say($"{e.Label}, gone. {_listIdx + 1} of {_rings[_listCat].Count}.", "Nav");
                            return;
                        }
                        e.X = cx; e.Y = cy;
                        _rings[_listCat][_listIdx] = e;
                    }
                }
                catch { }
            }

            _held = true;
            _tileX = e.X;
            _tileY = e.Y;
            AssertMouse(map);

            string line = (prefix ?? "") + e.Label + (InSight(map, e.X, e.Y, px, py) ? "" : ", out of view")
                + Offset(e.X - px, e.Y - py) + $" {_listIdx + 1} of {_rings[_listCat].Count}.";
            Scaffold.SpeechService.Say(line, "Nav");

            object t = TileAt(map, e.X, e.Y);
            string inspect = t != null ? S(Seams.MapTile_getInspectDescription, t) : null;
            if (!string.IsNullOrWhiteSpace(inspect)) ReviewLayer.NotePanel("Inspect", inspect);
        }

        /// <summary>The beacon: party stepped while an entity is landed —
        /// re-speak its offset, terse.</summary>
        private static void SpeakBeacon(object map, int px, int py)
        {
            try
            {
                var e = _rings[_listCat][_listIdx];
                int ex = e.X, ey = e.Y;
                if (e.Tracked != null)
                {
                    ex = (int)Seams.SkaldWorldObject_getTileX.Invoke(e.Tracked, null);
                    ey = (int)Seams.SkaldWorldObject_getTileY.Invoke(e.Tracked, null);
                }
                string off = Offset(ex - px, ey - py);
                Scaffold.SpeechService.SayQueued(off.TrimStart(',', ' '), "Nav");
            }
            catch { }
        }

        private static void CloseList(bool announce)
        {
            _listOpen = false;
            _rings = null;
            _listIdx = -1;
            _swallowTailFrame = Time.frameCount + 2;
            _announcedActive = false;   // the explicit close IS the off edge
            if (announce) Scaffold.SpeechService.Say("POI list closed.", "Nav");
        }

        /// <summary>Silent structural close (map change, combat). Leaves the
        /// edge-announcer flag alone: if the list was audible-open, Tick's
        /// next edge pass speaks the "POI list closed." for it.</summary>
        internal static void CloseListSilent()
        {
            _listOpen = false;
            _rings = null;
            _listIdx = -1;
        }
    }
}

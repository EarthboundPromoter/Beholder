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
    /// (never setSpotted, never getAccessibleTilesFromParty, never getParty)
    /// with ONE named exception: MapTile.getInventory() lazily allocates an
    /// empty Inventory on first read — benign, and the game's own draw loop
    /// performs the identical read on viewport tiles (MapIllustrator:913);
    /// we make it at browse cadence only.
    ///
    /// The POI list (owner redesign 2026-08-19): persistent and DYNAMIC.
    /// It survives interactions; it suspends (never dies) when a popup or a
    /// non-overland state takes over, announcing every on/off edge ("POI
    /// list active." / "POI list closed."), and resumes on return to
    /// overland with rings rebuilt from live truth and the selection
    /// re-anchored by identity. Combat is the one force-close (deployment
    /// needs the free mouse). Rings go stale on the game's own mutation
    /// clock — the step commit plus player action keys, never a timer — and
    /// rebuild lazily at the next browse press (every landing reads live
    /// truth; no per-step sweep).
    /// Ring membership mirrors the renderer: the removed-prop gate
    /// (shouldBeRemovedFromGame, the picked-up-item fix), hidden, undrawn.
    /// Unlocked empty containers carry ", empty" and sink to the ring tail.
    /// P re-anchors the cursor on the party tile from any overland context.
    ///
    /// Passive awareness (owner design 2026-08-21; config
    /// Overland.PassiveAwareness): the "enters view" layer. One membership
    /// sweep per world mutation (step commit, action key, excursion
    /// return), diffed by identity against the previous sweep; arrivals
    /// accumulate and flush as ONE category-counts line ("2 hostiles,
    /// 1 loot.") on the event speech lane. Map entry speaks a full census
    /// ("In view: ...") instead. Viewport-edge novelty is the RULE: leaving
    /// and re-entering the window re-announces (stable geography the player
    /// measures travel against). While passive is on the POI list rides the
    /// same sweeps and is step-fresh by construction.
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
            public bool Synthetic;       // edge exits: the anchor tile floats with the viewport
        }

        private static ConfigEntry<bool> _cfgCloseOnUse;

        internal static void BindConfig(ConfigFile config)
        {
            _cfgCloseOnUse = config.Bind("Overland", "POIListCloseOnUse", false,
                "Close the POI list every time Z activates an entry (default: the list stays open and "
                + "suspends/resumes around whatever the interaction opens).");
            _cfgPassive = config.Bind("Overland", "PassiveAwareness", true,
                "Announce what just came into view as category counts ('2 hostiles, 1 loot.') on every "
                + "step or action, and speak an 'In view:' census on map entry. Deliberately pays one POI "
                + "ring sweep per step while on (reverses the 2026-08-19 lazy-rebuild ruling for this "
                + "feature; sweep timing logged on the PA channel). Off restores browse-time-only rebuilds.");
            _cfgParity = config.Bind("Logging", "PassiveParityCheck", false,
                "Verification of the typed POI ring builder against the original reflective one (sweep-cost "
                + "job 2026-09-03): every ring build runs both and logs any membership difference as a "
                + "warning on the PA channel. Costs the old builder's milliseconds per sweep; leave off "
                + "except for the check ride.");
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
        private static readonly EatTail _tail = new EatTail();   // one game tick after a list close
        private static bool _ringsDirty;            // world may have mutated since the last build;
                                                    // rings refresh at the next browse press (Sonnet
                                                    // SHOULD-FIX 2026-08-19: no per-step rebuild —
                                                    // an auto-walk would pay it per path node)
        private static bool _announcedActive;       // the on/off edge announcer's memory
        private static long _activeKey = -1;        // moment-stamped modality cache (TickClock.MemoKey: tick OR frame change recomputes; predicates
        private static bool _activeCache;           // may run before Tick in a frame)

        public static bool ListOpen => _listOpen;

        /// <summary>The list is open AND owns input right now — overland, no
        /// popup and no selector grid over it (grid-open is modal per the
        /// WP9 ruling; its only keyboard dismiss is the native Escape, which
        /// a swallowing list would eat — Sonnet MUST-FIX 2026-08-19). While
        /// suspended, every key predicate stands down so popups, grids, and
        /// other states keep their native input.</summary>
        private static bool ActiveNow()
        {
            if (Scaffold.TickClock.MemoKey != _activeKey)
            {
                _activeKey = Scaffold.TickClock.MemoKey;
                _activeCache = _listOpen && InOverland() && CurrentMap() != null
                    && !PopupUp() && !Patches.GridNavigationPatch.GridActive();
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
            if (!(_tail.Holds || (_listOpen && ActiveNow()))) return false;
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

        // (SuppressButtonB retired 2026-08-21: R16 unbound the face-button
        // keyboard feeds mod-wide — Backspace no longer fires B anywhere.)

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
                    ResetPassive();   // fresh identity sets; the census speaks after the mount settles
                }

                // ---- Modality edges (run in every state) ----
                bool active = _listOpen && map != null && !PopupUp()
                    && !Patches.GridNavigationPatch.GridActive();
                _activeKey = Scaffold.TickClock.MemoKey;
                _activeCache = active;
                if (active && !_announcedActive)
                {
                    // Resume from an excursion: one announcement; the rings
                    // are marked stale and refresh at the next browse press.
                    // (Explicit K opens announce for themselves and pre-set
                    // the flag — they never land here.)
                    _ringsDirty = true;
                    if (PartyPos(map, out int rx, out int ry))
                    {
                        _lastPartyX = rx; _lastPartyY = ry;
                    }
                    _announcedActive = true;
                    Scaffold.Log.Debug("Mode", "POIList resumed");
                    Scaffold.SpeechService.SayQueued("POI list active.", "Nav");
                }
                else if (!active && _announcedActive)
                {
                    _announcedActive = false;
                    Scaffold.Log.Debug("Mode",
                        _listOpen ? "POIList suspended" : "POIList closed (structural)");
                    Scaffold.SpeechService.SayQueued("POI list closed.", "Nav");
                }

                if (map == null) return;
                if (PopupUp() || Patches.GridNavigationPatch.GridActive()) return;

                // ---- Staleness clock + beacon (overland, unobstructed) ----
                // The world only mutates on player actions (step commits,
                // bump interacts, clicks) — there is nothing to poll between
                // them. List-only mode: the edges only MARK the rings dirty
                // and the rebuild runs at the next browse press, so a march
                // never pays the ring sweep per tile (Sonnet SHOULD-FIX
                // 2026-08-19). Passive awareness DELIBERATELY reverses that
                // for its own clock — detection per step IS the feature
                // (owner ruling 2026-08-21) — so the block now runs for
                // list-open OR passive-on. The beacon stays live regardless:
                // tracked entities re-read their own coordinates.
                // Reachability's own mutation marks — deliberately UNGATED
                // (the rings block below is list/passive-gated; the reach set
                // must stay honest even with both off). A bool per keypress;
                // the fill itself is lazy.
                if (Input.GetKeyDown(KeyCode.Z) || Input.GetKeyDown(KeyCode.X)
                    || Input.GetKeyDown(KeyCode.Space)
                    || Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.A)
                    || Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.D))
                    TileReach.MarkDirty();

                bool passiveOn = _cfgPassive != null && _cfgPassive.Value;
                if ((_listOpen || passiveOn) && PartyPos(map, out int px, out int py))
                {
                    if (Input.GetKeyDown(KeyCode.Z) || Input.GetKeyDown(KeyCode.X)
                        || Input.GetKeyDown(KeyCode.Space)
                        || Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.A)
                        || Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.D))
                    {
                        _ringsDirty = true;
                        MarkPassiveDirty();
                    }

                    bool moved = (_lastPartyX != px || _lastPartyY != py) && _lastPartyX != int.MinValue;
                    if (moved)
                    {
                        _ringsDirty = true;
                        MarkPassiveDirty();
                        // Beacon: party stepped while an entity is landed —
                        // but SILENT while an auto-walk course is running (42
                        // offsets in 5 seconds on the ride — Opus log review
                        // 2026-08-19, owner-ruled). The arrival step's course
                        // is already empty, so arrival speaks naturally.
                        if (_listOpen && _listIdx >= 0 && !PartyCourseActive(map)) SpeakBeacon(map, px, py);
                    }
                    _lastPartyX = px; _lastPartyY = py;
                }

                if (passiveOn) PassiveTick(map);

                if (!_held) return;
                AssertMouse(map);
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("Cursor", ex.Message);
            }
        }

        /// <summary>Refresh the rings if the staleness clock marked them
        /// dirty — called at every browse press, so every landing reads live
        /// truth without any per-step sweep.</summary>
        private static void EnsureFresh(object map)
        {
            if (!_ringsDirty || _rings == null) return;
            if (!PartyPos(map, out int px, out int py)) return;
            _ringsDirty = false;
            RebuildRings(map, px, py);
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
            ApplyRings(BuildRings(map, px, py));
        }

        /// <summary>Install freshly-built rings into the open list, keeping
        /// RebuildRings' contract: selection re-anchored by identity, drained
        /// page auto-advanced. Split out 2026-08-21 so the passive-awareness
        /// sweep can feed the list without paying a second sweep.</summary>
        private static void ApplyRings(List<Entry>[] newRings)
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
                _rings[i] = newRings[i];
            Scaffold.Log.Debug("POI",
                $"rebuild h={_rings[0].Count} n={_rings[1].Count} l={_rings[2].Count}"
                + $" o={_rings[3].Count} x={_rings[4].Count} hadSel={hadSel}");

            if (!hadSel)
            {
                if (_listIdx >= _rings[_listCat].Count) _listIdx = -1;
            }
            else
            {
                var ring = _rings[_listCat];
                int found = trackedSel != null
                    ? ring.FindIndex(e => ReferenceEquals(e.Tracked, trackedSel))
                    : ring.FindIndex(e => e.Tracked == null && e.X == selX && e.Y == selY);
                _listIdx = found;
            }

            // The list never RESTS on an emptied page (owner ruling
            // 2026-08-19): if this category drained and another has content,
            // move there silently; the next landing carries its category
            // prefix so the player learns where they are.
            if (_rings[_listCat].Count == 0)
            {
                for (int step = 1; step < 5; step++)
                {
                    int cat = (_listCat + step) % 5;
                    if (_rings[cat].Count > 0)
                    {
                        _listCat = cat;
                        _listIdx = -1;
                        _prefixNextLand = true;
                        break;
                    }
                }
            }
        }

        private static bool _prefixNextLand;

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
            TileReach.MarkDirty(); // same rationale as the passive mark below
            MarkPassiveDirty();   // excursions mutate the world off the step
                                  // clock (dialogue spawns, combat outcomes);
                                  // the first clean overland frame re-sweeps
                                  // — an unchanged world diffs to silence.
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
            if (Input.GetKeyDown(KeyCode.V)) { SpeakPath(map); return true; }

            return false;
        }

        /// <summary>V: the path readout (owner design 2026-09-01, from
        /// day-one player feedback — the party routing around closed doors
        /// through four open ones with no warning). The course the game's own
        /// pathfinder would walk from the party to the cursor tile, computed
        /// side-effect-free: NavigationTools.findPath RETURNS a course
        /// without assigning it — only setPath commits one to the party.
        /// Mirrors findPathToMouseTile exactly (same water/land flags, same
        /// impassable-prop root-tile retry), so the readout is the click's
        /// truth. Works with the POI list open — the landed entry's tile IS
        /// the cursor tile. Grammar (owner ruling, RW3 lineage): direction
        /// then count per leg, comma-joined, "arrive" tail —
        /// "North 4, east 2, arrive."</summary>
        private static void SpeakPath(object map)
        {
            try
            {
                if (!PartyPos(map, out int px, out int py)) return;
                int tx = _held ? _tileX : px, ty = _held ? _tileY : py;
                if (tx == px && ty == py)
                { Scaffold.SpeechService.Say("Here.", "Nav"); return; }

                var grid = Seams.Map_tileGrid?.GetValue(map) as MapTileGrid;
                var tiles = grid == null ? null
                    : Seams.MapTileGrid_tileMap?.GetValue(grid) as MapTile[,];
                MapTile target = grid == null ? null : grid.getTile(tx, ty);
                if (tiles == null || target == null)
                { Scaffold.SpeechService.Say("No path.", "Nav"); return; }

                bool traverseWater = false;
                try { traverseWater = MainControl.getDataControl()?.getParty()?.canTraverseWater() ?? false; }
                catch { }
                bool traverseLand = !target.isWater() || target.hasVehicle();

                NavigationCourse course = NavigationTools.findPath(px, py, tx, ty,
                    traverseWater, traverseLand, tiles, returnApproximate: false);
                if (course == null || !course.hasNodes())
                {
                    // The click's own fallback: an impassable prop is "at"
                    // its root tile (findPathToMouseTile lines 213-221).
                    MapTile root = target.getPropRootTile();
                    if (root != null && !ReferenceEquals(root, target))
                        course = NavigationTools.findPath(px, py, root.getTileX(), root.getTileY(),
                            traverseWater, traverseLand, tiles, returnApproximate: false);
                }
                if (course == null || !course.hasNodes())
                { Scaffold.SpeechService.Say("No path.", "Nav"); return; }

                Scaffold.SpeechService.Say(ComposeCourse(course, px, py), "Nav");
            }
            catch (System.Exception ex)
            {
                Scaffold.Log.Throttled("Path", ex.Message);
            }
        }

        private static string ComposeCourse(NavigationCourse course, int px, int py)
        {
            var legs = new List<string>();
            int cx = px, cy = py;
            string dir = null; int run = 0;
            foreach (var p in course.course)
            {
                string d = p.Y > cy ? "north" : p.Y < cy ? "south"
                    : p.X > cx ? "east" : p.X < cx ? "west" : null;
                cx = p.X; cy = p.Y;
                if (d == null) continue;
                if (d == dir) { run++; continue; }
                if (dir != null) legs.Add($"{dir} {run}");
                dir = d; run = 1;
            }
            if (dir != null) legs.Add($"{dir} {run}");
            if (legs.Count == 0) return "Here.";
            string line = string.Join(", ", legs.ToArray()) + ", arrive.";
            return char.ToUpperInvariant(line[0]) + line.Substring(1);
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
            string coords = TileArtTable.CoordTail(tx, ty);
            object tile = TileAt(map, tx, ty);
            if (tile == null)
            {
                Scaffold.SpeechService.Say("Nothing." + Offset(tx - px, ty - py) + coords, "Nav");
                return;
            }

            if (!B(Seams.MapTile_isSpotted, tile))
            {
                Scaffold.SpeechService.Say("Unexplored." + Offset(tx - px, ty - py) + coords + (countTail ?? ""), "Nav");
                return;
            }

            string label = TileLabel(map, tile, tx, ty, px, py);
            string qualifier = InSight(map, tx, ty, px, py) ? "" : ", out of view";
            string reach = TileReach.Verdict(map, tile, tx, ty);
            Scaffold.SpeechService.Say(label + qualifier + reach + Offset(tx - px, ty - py) + coords + (countTail ?? ""), "Nav");

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

            // Tile-art transcode rung (owner go 2026-08-29): the classification
            // job's noun for the tile's topmost speakable layer, replacing the
            // bare flag words below. Everything the game labels won already,
            // above — primacy by ladder order. Owner revision same day
            // (post-release player feedback): the noun alone confused spatial
            // reading — the flag word LEADS again, uniformly ("Blocked, birch
            // trees" / "Open, cobbles"), replacing the trailing qualifier and
            // its wall exemption. Water keeps its bare noun: the old ladder's
            // Water rung outranked Blocked (passability differs afoot vs
            // asail), so a flag prefix there would mislead sailing. Null
            // (unknown art, config off, seams missing) keeps the flag words —
            // the zero-regression floor. Combat's ladder deliberately has no
            // such rung (owner ruling: clear/blocked stays).
            string art = TileArtTable.LabelFor(tile, out bool wallish);
            if (!string.IsNullOrEmpty(art))
            {
                if (B(Seams.MapTile_isWater, tile))
                    return char.ToUpperInvariant(art[0]) + art.Substring(1);
                string flag = B(Seams.MapTile_isPassable, tile) ? "Open" : "Blocked";
                return flag + ", " + art;
            }

            if (B(Seams.MapTile_isWater, tile)) return "Water";
            if (B(Seams.MapTile_isVoidTile, tile)) return "Nothing";
            if (!B(Seams.MapTile_isPassable, tile)) return "Blocked";
            return "Open";
        }

        private static bool GroundItems(object tile)
        {
            // Mirrors the renderer's ground-item icon gate: non-empty inventory
            // on a passable tile (the illustrator runs this same read per frame).
            // NOTE: getInventory() lazily ALLOCATES on first read — the one
            // sanctioned side effect (see class doc); the game's draw loop
            // makes the same read on these tiles anyway.
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
        //
        // The ring builder (typed single pass; sweep-cost job 2026-09-03).
        // One walk over the 23×17 viewport classifies every tile into all five
        // rings with DIRECT calls on the referenced game assembly. The WP11
        // builder (BuildRingLegacy below — kept PERMANENTLY as the fallback
        // and the parity reference, owner ruling 2026-09-03) walked the
        // viewport once per category through
        // MethodInfo.Invoke, plus once more per map edge with an edge id:
        // 9,000–17,000 reflective calls and about a megabyte of boxing
        // garbage per sweep — 4–11 ms on the owner's 60 Hz ride, a dropped
        // frame on every sweep at 240 Hz. Membership, labels, ordering
        // (RingOrder / UnseenOrder, shared with the legacy builder), the
        // renderer's prop gate, the unseen tail, and the one sanctioned side
        // effect (getInventory's lazy allocation — the renderer's own read)
        // are unchanged; Logging.PassiveParityCheck proves it on a ride.
        //
        // Containment: every typed member reference lives in BuildRingsTyped
        // and PropEntryTyped, reached only inside BuildRings' try. A game
        // update that removes one surfaces as a MissingMethodException at
        // that method's JIT — caught at the call in BuildRings, one Error
        // line, and the legacy builder answers for the rest of the session.

        private static readonly Comparison<Entry> RingOrder = (a, b) =>
            a.Deprioritized != b.Deprioritized ? (a.Deprioritized ? 1 : -1)
            : a.Dist != b.Dist ? a.Dist - b.Dist : (a.Y != b.Y ? b.Y - a.Y : a.X - b.X);
        private static readonly Comparison<Entry> UnseenOrder = (a, b) => a.Dist - b.Dist;

        private static ConfigEntry<bool> _cfgParity;
        private static bool ParityOn => _cfgParity != null && _cfgParity.Value;
        private static bool _typedFailed;
        private static int _typedFaults;         // consecutive non-JIT faults; reset by a clean build
        private const int TypedFaultLimit = 3;
        private static double _lastRingsMs;      // the typed build alone, for the PA sweep line
        private static int _parityLines;

        /// <summary>All five rings for the party at (px,py): typed builder,
        /// legacy fallback, optional parity check. Never null, never throws.
        /// <paramref name="onlyCat"/> (a scan's single category) matters only
        /// on the fallback: the typed pass builds all five for free, the
        /// legacy pass costs a viewport walk per ring, so a scan in degraded
        /// mode pays one walk — the pre-rewrite cost — not five (Opus review
        /// 2026-09-03).</summary>
        private static List<Entry>[] BuildRings(object map, int px, int py, int onlyCat = -1)
        {
            List<Entry>[] rings = null;
            if (!_typedFailed)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { rings = BuildRingsTyped(map, px, py); _typedFaults = 0; }
                catch (Exception ex)
                {
                    rings = null;
                    // The JIT class (a game update removed a member: Missing*
                    // or TypeLoad) is permanent for the session. Anything
                    // else is one bad tile or prop on THIS build — the
                    // reflective builder answers this once (its per-read
                    // try/catch tolerates the same fault) and the typed path
                    // gets the next call — unless it keeps faulting: a fault
                    // that is a property of the map would otherwise pay the
                    // pre-rewrite sweep cost on every step at Debug-only
                    // visibility (Sonnet SHOULD-FIX 2026-09-03), so the third
                    // consecutive fault escalates to the same Error + disable.
                    bool jit = ex is MissingMemberException || ex is TypeLoadException;
                    if (jit || ++_typedFaults >= TypedFaultLimit)
                    {
                        _typedFailed = true;
                        Plugin.Logger?.LogError($"[POI] Typed ring builder unavailable ({ex.GetType().Name}: {ex.Message}"
                            + (jit ? "" : $"; {_typedFaults} consecutive builds faulted")
                            + "); the reflective builder answers for the rest of this session — the passive layer pays the pre-rewrite sweep cost");
                    }
                    else Scaffold.Log.Throttled("POI:typed", $"{ex.GetType().Name}: {ex.Message} (reflective builder answered this build; fault {_typedFaults} of {TypedFaultLimit})");
                }
                _lastRingsMs = rings == null ? -1
                    : (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
            else _lastRingsMs = -1;
            if (rings == null) return BuildRingsLegacy(map, px, py, onlyCat);
            if (ParityOn) CheckParity(rings, BuildRingsLegacy(map, px, py));
            return rings;
        }

        /// <summary>The reflective builder, all five rings — or just
        /// <paramref name="onlyCat"/> with the other four empty.</summary>
        private static List<Entry>[] BuildRingsLegacy(object map, int px, int py, int onlyCat = -1)
        {
            var rings = new List<Entry>[5];
            for (int i = 0; i < 5; i++)
                rings[i] = onlyCat < 0 || i == onlyCat ? BuildRingLegacy(map, (Category)i, px, py) : new List<Entry>();
            return rings;
        }

        /// <summary>The typed single pass. Same tile order as the legacy
        /// builder (x outer, y inner), same per-category tests in the same
        /// order, edge exits appended N,S,E,W before the sort, unseen tail
        /// after it — identical input sequences to the same comparator give
        /// identical rings.</summary>
        private static List<Entry>[] BuildRingsTyped(object mapObj, int px, int py)
        {
            var map = (Map)mapObj;
            var rings = new List<Entry>[5];
            for (int i = 0; i < 5; i++) rings[i] = new List<Entry>();
            var hostiles = rings[(int)Category.Hostiles];
            var neutrals = rings[(int)Category.Neutrals];
            var loot = rings[(int)Category.Loot];
            var objects = rings[(int)Category.Objects];
            var exits = rings[(int)Category.Exits];
            var unseenTail = new List<Entry>();

            int vx = map.getViewportX();
            int vy = map.getViewportY();

            // Edge exits: one synthetic entry per map-edge direction whose
            // edge id is set, at the nearest passable border tile in the
            // window (first found wins ties). Judged on EVERY valid tile,
            // spotted or not — the legacy AddEdge had no spotted gate.
            bool north = !string.IsNullOrEmpty(map.northernEdgeMapId);
            bool south = !string.IsNullOrEmpty(map.southernEdgeMapId);
            bool east = !string.IsNullOrEmpty(map.easternEdgeMapId);
            bool west = !string.IsNullOrEmpty(map.westernEdgeMapId);
            bool anyEdge = north || south || east || west;
            Entry bestN = default, bestS = default, bestE = default, bestW = default;
            bool foundN = false, foundS = false, foundE = false, foundW = false;

            for (int tx = vx - 11; tx <= vx + 11; tx++)
            {
                for (int ty = vy - 8; ty <= vy + 8; ty++)
                {
                    if (!map.isTileValid(tx, ty)) continue;
                    MapTile tile = map.getTile(tx, ty);
                    if (tile == null) continue;
                    int dist = Math.Abs(tx - px) + Math.Abs(ty - py);

                    if (anyEdge)
                    {
                        int mtx = tile.getTileX(), mty = tile.getTileY();
                        bool onW = west && mtx == 0;
                        bool onS = south && mty == 0;
                        bool onE = east && !map.isTileValid(mtx + 1, mty);
                        bool onN = north && !map.isTileValid(mtx, mty + 1);
                        if ((onW || onS || onE || onN) && tile.isPassable())
                        {
                            if (onN && (!foundN || dist < bestN.Dist)) { bestN = new Entry { X = tx, Y = ty, Label = "North edge", Dist = dist, Synthetic = true }; foundN = true; }
                            if (onS && (!foundS || dist < bestS.Dist)) { bestS = new Entry { X = tx, Y = ty, Label = "South edge", Dist = dist, Synthetic = true }; foundS = true; }
                            if (onE && (!foundE || dist < bestE.Dist)) { bestE = new Entry { X = tx, Y = ty, Label = "East edge", Dist = dist, Synthetic = true }; foundE = true; }
                            if (onW && (!foundW || dist < bestW.Dist)) { bestW = new Entry { X = tx, Y = ty, Label = "West edge", Dist = dist, Synthetic = true }; foundW = true; }
                        }
                    }

                    if (!tile.isSpotted()) continue;

                    // Characters: hostiles / neutrals by identity; the
                    // unseen-icon state tails the hostiles ring (caution bias).
                    Character ch = tile.getLiveCharacter();
                    if (ch != null && !ch.isPC())
                    {
                        bool identifiable = tile.isIlluminated() || ch.isSpotted();
                        if (!identifiable)
                        {
                            if (!tile.isConcealment())
                                unseenTail.Add(new Entry { X = tx, Y = ty, Label = "Something unseen", Tracked = ch, Dist = dist });
                        }
                        else
                        {
                            string name = Patches.TextCleaner.CleanText(ch.getName() ?? "Someone");
                            (ch.isHostile() ? hostiles : neutrals).Add(new Entry { X = tx, Y = ty, Label = name, Tracked = ch, Dist = dist });
                        }
                    }

                    // The prop through the renderer's gate (drawProps):
                    // removed-from-game, hidden, undrawn.
                    Prop prop = tile.getPropOrGuestProp();
                    if (prop != null && (prop.shouldBeRemovedFromGame() || prop.isHidden() || prop.shouldNotBeDrawn()))
                        prop = null;

                    // Loot: containers and pickups; looted containers labeled
                    // and sunk (owner ruling 2026-08-19); else ground items.
                    if (prop != null && (prop is PropCont || prop is PropPickup))
                    {
                        Entry pe = PropEntryTyped(prop, tx, ty, dist);
                        if (prop is PropCont && !((PropLockable)prop).isLocked() && tile.getInventory().isEmpty())
                        {
                            pe.Label += ", empty";
                            pe.Deprioritized = true;
                        }
                        loot.Add(pe);
                    }
                    else if (tile.isPassable() && !tile.getInventory().isEmpty())
                        loot.Add(new Entry { X = tx, Y = ty, Label = "Items", Dist = dist });

                    // Objects: every other named prop.
                    if (prop != null && !(prop is PropWarp || prop is PropCont || prop is PropPickup
                        || prop is PropBeacon || prop is PropSpawner || prop is PropTrigger))
                    {
                        string pn = Patches.TextCleaner.CleanText(prop.getName() ?? "");
                        // pn is non-empty here, so this IS PropEntryTyped's
                        // label — built once instead of cleaning the name
                        // twice (the legacy path pays the second CleanText).
                        if (!string.IsNullOrWhiteSpace(pn))
                            objects.Add(new Entry { X = tx, Y = ty, Label = pn, Dist = dist });
                    }

                    // Exits: warps, nested-map tiles, ships.
                    if (prop != null && prop is PropWarp)
                        exits.Add(PropEntryTyped(prop, tx, ty, dist));
                    else
                    {
                        string nested = tile.getNestedMapId();
                        if (!string.IsNullOrEmpty(nested))
                        {
                            string pn = prop != null ? Patches.TextCleaner.CleanText(prop.getName() ?? "") : "";
                            exits.Add(new Entry { X = tx, Y = ty, Label = string.IsNullOrWhiteSpace(pn) ? "Entrance" : pn, Dist = dist });
                        }
                        else if (tile.hasVehicle())
                            exits.Add(new Entry { X = tx, Y = ty, Label = "Ship", Dist = dist });
                    }
                }
            }

            if (foundN) exits.Add(bestN);
            if (foundS) exits.Add(bestS);
            if (foundE) exits.Add(bestE);
            if (foundW) exits.Add(bestW);
            for (int i = 0; i < 5; i++) rings[i].Sort(RingOrder);
            if (unseenTail.Count > 0)
            {
                unseenTail.Sort(UnseenOrder);
                hostiles.AddRange(unseenTail);
            }
            return rings;
        }

        private static Entry PropEntryTyped(Prop prop, int tx, int ty, int dist)
        {
            string pn = Patches.TextCleaner.CleanText(prop.getName() ?? "");
            return new Entry { X = tx, Y = ty, Label = string.IsNullOrWhiteSpace(pn) ? "Object" : pn, Dist = dist };
        }

        /// <summary>The parity receipt (Logging.PassiveParityCheck): the
        /// first differing entry per ring as a Warning, one Debug summary
        /// per build. Capped so a systematic difference costs lines, not the
        /// log.</summary>
        private static void CheckParity(List<Entry>[] typed, List<Entry>[] legacy)
        {
            bool allEqual = true;
            for (int c = 0; c < 5; c++)
            {
                var a = typed[c];
                var b = legacy[c];
                int n = Math.Max(a.Count, b.Count);
                for (int i = 0; i < n; i++)
                {
                    string diff = null;
                    if (i >= a.Count) diff = "typed lacks " + Describe(b[i]);
                    else if (i >= b.Count) diff = "legacy lacks " + Describe(a[i]);
                    else if (!SameEntry(a[i], b[i])) diff = "typed " + Describe(a[i]) + " vs legacy " + Describe(b[i]);
                    if (diff == null) continue;
                    allEqual = false;
                    if (_parityLines++ < 40)
                        Plugin.Logger?.LogWarning($"[PA] parity MISMATCH {CategoryNames[c]} #{i}: {diff} (typed {a.Count}, legacy {b.Count})");
                    break;
                }
            }
            Scaffold.Log.Debug("PA", $"parity {(allEqual ? "equal" : "MISMATCH")} h={typed[0].Count}/{legacy[0].Count}"
                + $" n={typed[1].Count}/{legacy[1].Count} l={typed[2].Count}/{legacy[2].Count}"
                + $" o={typed[3].Count}/{legacy[3].Count} x={typed[4].Count}/{legacy[4].Count}");
        }

        private static bool SameEntry(Entry a, Entry b)
            => a.X == b.X && a.Y == b.Y && a.Dist == b.Dist && a.Deprioritized == b.Deprioritized
               && a.Synthetic == b.Synthetic && ReferenceEquals(a.Tracked, b.Tracked)
               && string.Equals(a.Label, b.Label, StringComparison.Ordinal);

        private static string Describe(Entry e)
            => $"({e.X},{e.Y}) '{e.Label}' d{e.Dist}{(e.Deprioritized ? " depr" : "")}{(e.Synthetic ? " syn" : "")}{(e.Tracked != null ? " tracked" : "")}";

        /// <summary>The WP11 reflective builder, one category per call.
        /// Retired from the hot path by the typed single pass above; kept
        /// PERMANENTLY as the fallback and the parity reference (owner ruling
        /// 2026-09-03): a game update that breaks the typed builder costs
        /// speed, not the passive layer, the K list, or the scans — and a
        /// scan in that mode pays one walk (BuildRings' onlyCat), the
        /// pre-rewrite cost. Note for parity rides: this builder answers
        /// false for a NULL type seam (IsType), so a missing prop-type seam
        /// makes IT the wrong side of a mismatch; the boot seam report names
        /// the seam.</summary>
        private static List<Entry> BuildRingLegacy(object map, Category cat, int px, int py)
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
            ring.Sort(RingOrder);
            if (unseenTail.Count > 0)
            {
                unseenTail.Sort(UnseenOrder);
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
                        best = new Entry { X = tx, Y = ty, Label = label, Dist = dist, Synthetic = true };
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
            var ring = BuildRings(map, px, py, (int)cat)[(int)cat];
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

            var built = BuildRings(map, px, py);
            var census = new List<string>();
            int unseen = 0;
            for (int i = 0; i < 5; i++)
            {
                int n = built[i].Count;
                if (i == (int)Category.Hostiles)
                {
                    unseen = built[i].FindAll(e => e.Label == "Something unseen").Count;
                    n -= unseen;
                }
                if (n > 0) census.Add($"{n} {(n == 1 ? CategoryNames[i].TrimEnd('s') : CategoryNames[i])}");
            }
            if (unseen > 0) census.Add($"{unseen} unseen");

            if (census.Count == 0)
            {
                Scaffold.SpeechService.Say("Nothing nearby.", "Nav");
                _rings = null;   // closed-list invariant kept explicit (was the pre-rewrite behaviour)
                return;
            }

            _rings = built;
            _listOpen = true;
            _listIdx = -1;
            _listCat = 0;
            while (_listCat < 4 && _rings[_listCat].Count == 0) _listCat++;
            _lastPartyX = px; _lastPartyY = py;
            _ringsDirty = false;       // just built
            _announcedActive = true;   // the explicit open IS the on edge
            Scaffold.Log.Debug("Mode", "POIList opened (K)");
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
                else _ringsDirty = true;
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
            EnsureFresh(map);
            var ring = _rings[_listCat];
            if (ring.Count == 0) { Scaffold.SpeechService.Say("Empty.", "Nav"); return; }
            int next = _listIdx < 0 ? (dir > 0 ? 0 : ring.Count - 1) : _listIdx + dir;
            if (next < 0) { Scaffold.SpeechService.Say("Start of list.", "Nav"); return; }
            if (next >= ring.Count) { Scaffold.SpeechService.Say("End of list.", "Nav"); return; }
            _listIdx = next;
            Land(map, prefix: null);
        }

        /// <summary>Left/Right: visit EVERY category in order, empty ones
        /// included, announcing "Hostiles: none." on the empty stops (owner
        /// ruling 2026-08-19 — skipping them made empty pages a trap; every
        /// press also refreshes via EnsureFresh, so cycling IS the manual
        /// refresh).</summary>
        private static void ListCat(object map, int dir)
        {
            EnsureFresh(map);
            _listCat = (_listCat + dir + 5) % 5;
            _listIdx = _rings[_listCat].Count > 0 ? 0 : -1;
            string catName = char.ToUpper(CategoryNames[_listCat][0]) + CategoryNames[_listCat].Substring(1);
            if (_listIdx < 0) { Scaffold.SpeechService.Say($"{catName}: none.", "Nav"); return; }
            Land(map, prefix: $"{catName}: ");
        }

        /// <summary>Land on the current list entry: zip the mouse, speak,
        /// panel. Entities re-validate at speech time.</summary>
        private static void Land(object map, string prefix)
        {
            if (prefix == null && _prefixNextLand)
            {
                // First landing after an automatic page switch: say where we are.
                prefix = char.ToUpper(CategoryNames[_listCat][0]) + CategoryNames[_listCat].Substring(1) + ": ";
            }
            _prefixNextLand = false;
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

        /// <summary>True while the player party is mid auto-walk (the game's
        /// own course truth). Failure degrades to false — the beacon speaks,
        /// the pre-fix behavior.</summary>
        private static bool PartyCourseActive(object map)
        {
            try
            {
                if (Seams.Map_playerParty == null || Seams.Party_navigationCourseHasNodes == null) return false;
                object party = Seams.Map_playerParty.GetValue(map);
                return party != null && (bool)Seams.Party_navigationCourseHasNodes.Invoke(party, null);
            }
            catch { return false; }
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

        // ---- Passive awareness (owner design 2026-08-21) ----
        // One membership sweep per world mutation, diffed by identity against
        // the previous sweep. Arrivals accumulate and flush as ONE line of
        // category counts through the EVENT speech lane (SayQueuedEvent — no
        // render-diff dedup: two honest "1 hostile." lines minutes apart must
        // both speak; overflow coalesces, never drops). Viewport-edge novelty
        // is the rule: the previous-sweep sets are the ONLY state, so leaving
        // and re-entering the window re-announces by construction.

        private static ConfigEntry<bool> _cfgPassive;

        private sealed class RefEq : IEqualityComparer<object>
        {
            public static readonly RefEq Instance = new RefEq();
            bool IEqualityComparer<object>.Equals(object a, object b) { return ReferenceEquals(a, b); }
            int IEqualityComparer<object>.GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }

        private static HashSet<object> _paSeenTracked = new HashSet<object>(RefEq.Instance);
        private static HashSet<long> _paSeenFixed = new HashSet<long>();
        private static bool _paSeeded;               // census spoken for this map
        // Passive timing is in GAME TICKS (TickClock.Moment; audit 2026-09-03):
        // the marks land on the keypress frame, before the tick that processes
        // the action, so "one later" must be one TICK — at 240 fps one render
        // frame later the world was still unprocessed and the sweep baked the
        // arrivals into the baseline. Constants unchanged (ticks = frames at 60 fps).
        private static long _paSeedEarliestMoment;   // census defers past the mount tick
        private static bool _paDirty;
        private static long _paDirtyMoment = -1;     // sweep waits one tick past the mark
        private static long _paEchoMoment = -1;      // one follow-up sweep after every dirty
                                                     // sweep (Sonnet SHOULD-FIX 2026-08-21:
                                                     // a same-tile interact has no movement
                                                     // event to re-arm the clock, so a
                                                     // mutation landing 2+ frames after the
                                                     // keypress would bake into the baseline
                                                     // silently or announce late on the next
                                                     // step; the echo catches it — idempotent,
                                                     // never self-rescheduling)
        private static readonly int[] _paPending = new int[6];   // 5 categories + unseen
        private static bool _paPendingAny;
        private static float _paLastAccumAt;

        private static void MarkPassiveDirty()
        {
            _paDirty = true;
            _paDirtyMoment = Scaffold.TickClock.Moment;
        }

        private static void ResetPassive()
        {
            _paSeenTracked.Clear();
            _paSeenFixed.Clear();
            _paSeeded = false;
            _paSeedEarliestMoment = Scaffold.TickClock.Moment + 1;
            _paDirty = false;
            _paEchoMoment = -1;
            Array.Clear(_paPending, 0, _paPending.Length);
            _paPendingAny = false;
        }

        /// <summary>Identity key for entries with no tracked reference.
        /// Prop/ground/exit entries are tile-fixed: (category, x, y).
        /// Synthetic edge exits FLOAT — their anchor tile slides along the
        /// border with the viewport — so they key by label instead: one
        /// "North edge" identity however the window moves.</summary>
        private static long FixedKey(int cat, Entry e)
        {
            if (e.Synthetic)
                return (1L << 60) | ((long)cat << 40) | (uint)e.Label.GetHashCode();
            return ((long)cat << 40) | ((long)(e.X & 0xFFFFF) << 20) | (uint)(e.Y & 0xFFFFF);
        }

        /// <summary>Per-frame passive driver (from Tick; overland, no popup,
        /// no grid). Seeds the map census, runs deferred-dirty sweeps, and
        /// flushes the arrivals accumulator after a burst-merge gap.</summary>
        private static void PassiveTick(object map)
        {
            if (!_paSeeded)
            {
                if (Scaffold.TickClock.Moment < _paSeedEarliestMoment) return;
                if (!PartyPos(map, out int sx, out int sy)) return;
                var rings = PassiveSweep(map, sx, sy, countArrivals: false);
                _paSeeded = true;
                _paDirty = false;
                var counts = new int[6];
                for (int i = 0; i < 5; i++) counts[i] = rings[i].Count;
                counts[5] = rings[(int)Category.Hostiles].FindAll(e => e.Label == "Something unseen").Count;
                counts[(int)Category.Hostiles] -= counts[5];
                string line = ComposeCounts(counts);
                // Census on the map edge (owner ruling 2026-08-21: full
                // enumeration of what is already in view and actionable, in
                // the same language). Queued — it lands behind the game's own
                // map-name announcement.
                Scaffold.SpeechService.SayQueuedEvent(
                    line == null ? "Nothing in view." : "In view: " + line + ".", "Passive");
                Scaffold.Log.Debug("PA", "census: " + (line ?? "empty"));
                return;
            }

            if (_paDirty && Scaffold.TickClock.Moment > _paDirtyMoment)
            {
                _paDirty = false;
                if (PartyPos(map, out int px, out int py))
                {
                    PassiveSweep(map, px, py, countArrivals: true);
                    _paEchoMoment = Scaffold.TickClock.Moment + 6;
                }
            }
            else if (_paEchoMoment >= 0 && Scaffold.TickClock.Moment >= _paEchoMoment)
            {
                _paEchoMoment = -1;
                if (PartyPos(map, out int ex, out int ey))
                    PassiveSweep(map, ex, ey, countArrivals: true);
            }

            // Burst merge: a held-key march lands arrivals across adjacent
            // steps — hold briefly so they speak as one line. Back off while
            // the speech queue is deep: the accumulator keeps merging, so
            // nothing drops, it just gets terser.
            if (_paPendingAny && Time.unscaledTime - _paLastAccumAt >= 0.35f
                && Scaffold.SpeechService.QueueDepth < 15)
                FlushPassive();
        }

        /// <summary>One full membership sweep: rebuild the identity sets,
        /// optionally count arrivals into the accumulator, and — when the
        /// POI list is open — apply the fresh rings to it (the list is
        /// step-fresh by construction while passive is on).</summary>
        private static List<Entry>[] PassiveSweep(object map, int px, int py, bool countArrivals)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var rings = BuildRings(map, px, py);
            var tracked = new HashSet<object>(RefEq.Instance);
            var fixedKeys = new HashSet<long>();
            bool any = false;
            for (int i = 0; i < 5; i++)
            {
                foreach (var e in rings[i])
                {
                    bool fresh;
                    if (e.Tracked != null)
                    {
                        fresh = !_paSeenTracked.Contains(e.Tracked);
                        tracked.Add(e.Tracked);
                    }
                    else
                    {
                        long k = FixedKey(i, e);
                        fresh = !_paSeenFixed.Contains(k);
                        fixedKeys.Add(k);
                    }
                    if (fresh && countArrivals)
                    {
                        if (e.Label == "Something unseen") _paPending[5]++;
                        else _paPending[i]++;
                        any = true;
                    }
                }
            }
            _paSeenTracked = tracked;
            _paSeenFixed = fixedKeys;
            if (_listOpen) { ApplyRings(rings); _ringsDirty = false; }
            if (any)
            {
                _paPendingAny = true;
                _paLastAccumAt = Time.unscaledTime;
            }
            // Every sweep logs its cost (the 2 ms threshold went with the
            // sweep-cost job 2026-09-03: the new number must be written
            // down). "rings" is the typed build alone; "sweep" adds the
            // identity diff and the parity build when that is on.
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Scaffold.Log.Debug("PA", $"sweep {ms:0.00}ms ({(_lastRingsMs < 0 ? "rings legacy" : $"rings {_lastRingsMs:0.00}ms")}{(ParityOn ? ", parity on" : "")})"
                + (any ? $" pending h={_paPending[0]} n={_paPending[1]} l={_paPending[2]}"
                       + $" o={_paPending[3]} x={_paPending[4]} u={_paPending[5]}" : ""));
            return rings;
        }

        /// <summary>The census/arrival language — the K list's own count
        /// grammar: "2 hostiles, 1 loot", unseen trailing. Null when all
        /// slots are zero.</summary>
        private static string ComposeCounts(int[] c)
        {
            var parts = new List<string>();
            for (int i = 0; i < 5; i++)
                if (c[i] > 0)
                    parts.Add($"{c[i]} {(c[i] == 1 ? CategoryNames[i].TrimEnd('s') : CategoryNames[i])}");
            if (c[5] > 0) parts.Add($"{c[5]} unseen");
            return parts.Count == 0 ? null : string.Join(", ", parts.ToArray());
        }

        private static void FlushPassive()
        {
            string line = ComposeCounts(_paPending);
            Array.Clear(_paPending, 0, _paPending.Length);
            _paPendingAny = false;
            if (line == null) return;
            Scaffold.SpeechService.SayQueuedEvent(line + ".", "Passive");
        }

        private static void CloseList(bool announce)
        {
            _listOpen = false;
            _rings = null;
            _listIdx = -1;
            _tail.Arm(2);               // one game tick (EatTail)
            _announcedActive = false;   // the explicit close IS the off edge
            Scaffold.Log.Debug("Mode", "POIList closed (explicit)");
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

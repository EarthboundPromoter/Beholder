using System.Collections.Generic;
using UnityEngine;

namespace SkaldAccessibility
{
    /// <summary>
    /// Overland reachability verdict (owner go 2026-08-29 — tier 1 of the
    /// wayfinding docket): a forward BFS from the party's tile over the game's
    /// own NavigationTools passability model, answering the one question
    /// "can walking alone get you there from here" — the binary the owner
    /// ruled for ("knowing the player has to do something other than wander
    /// around"). Intermediary counts don't matter; a region is either yours
    /// or it isn't.
    ///
    /// The model, mirrored from NavigationTools.NodeMap/findAStarPath:
    ///  - MapTile.isPassable() is the terrain+prop truth;
    ///  - water rides the live Party.canTraverseWater() flag, with the
    ///    pathfinder's own directed edge rule verbatim: from land you may
    ///    step onto water only where a vehicle sits (boarding); from water,
    ///    anywhere adjacent (disembarking);
    ///  - characters/parties are deliberately EXCLUDED (the pathfinder blocks
    ///    them because it answers "right now"; regions are structural — an
    ///    NPC in a doorway must not wall off a room);
    ///  - ONE divergence: a closed door that is neither locked nor hidden is
    ///    traversable (bump-opening is wandering, not "doing something");
    ///    locked doors and secret doors stay walls — locked is exactly the
    ///    owner's "something else required" class, and hidden matches the
    ///    renderer so nothing unrendered leaks.
    ///
    /// Recompute is lazy on the world-mutation clock (action/movement key
    /// edges + state transitions mark dirty; the fill runs at the next
    /// landing), plus self-healing triggers: map identity change, water-mode
    /// flip, and a party tile outside the cached set (scripted moves). A
    /// landing then answers O(1). Enterable tiles speak by their region;
    /// blocked/prop tiles use the game's own click-target rule (reachable at
    /// any enterable neighbor; SILENT when no neighbor is enterable at all —
    /// deep wall interiors, open water afoot — the label already carries
    /// those). Unexplored tiles never get here (SpeakTile's spotted gate).
    /// </summary>
    internal static class TileReach
    {
        private static BepInEx.Configuration.ConfigEntry<bool> _enabled;

        public static void BindConfig(BepInEx.Configuration.ConfigFile config)
        {
            _enabled = config.Bind("Tiles", "Reachability", true,
                "Append 'out of reach' to overland tile readouts when walking alone cannot reach "
                + "the tile from the party's position (a locked door, a climb, or another area "
                + "lies between). Unlocked doors count as walkable.");
        }

        private static bool[,] _reach;
        private static int _w, _h;
        private static readonly System.WeakReference _mapRef = new System.WeakReference(null);
        private static bool _dirty = true;
        private static bool _waterMode;

        public static void MarkDirty() { _dirty = true; }

        /// <summary>The verdict tail for a landed tile: ", out of reach" or
        /// "". Never throws; any failure is silence (the label stands alone).</summary>
        public static string Verdict(object mapObj, object tileObj, int x, int y)
        {
            try
            {
                if (_enabled != null && !_enabled.Value) return "";
                var tile = tileObj as MapTile;
                var grid = Seams.Map_tileGrid?.GetValue(mapObj) as MapTileGrid;
                if (tile == null || grid == null) return "";
                if (!EnsureFresh(mapObj, grid)) return "";
                if (x < 0 || y < 0 || x >= _w || y >= _h) return "";

                if (Enterable(tile))
                    return _reach[x, y] ? "" : ", out of reach";

                // The game's own click-target rule (isTargetNodeAcessible):
                // a blocked tile is "at" its enterable neighbors.
                bool anyEnterable = false, anyReached = false;
                Probe(grid, x + 1, y, ref anyEnterable, ref anyReached);
                Probe(grid, x - 1, y, ref anyEnterable, ref anyReached);
                Probe(grid, x, y + 1, ref anyEnterable, ref anyReached);
                Probe(grid, x, y - 1, ref anyEnterable, ref anyReached);
                if (!anyEnterable) return "";
                return anyReached ? "" : ", out of reach";
            }
            catch (System.Exception ex)
            {
                Scaffold.Log.Throttled("Reach", ex.Message);
                return "";
            }
        }

        private static void Probe(MapTileGrid grid, int x, int y,
            ref bool anyEnterable, ref bool anyReached)
        {
            if (x < 0 || y < 0 || x >= _w || y >= _h) return;
            MapTile t = grid.getTile(x, y);
            if (t == null || !Enterable(t)) return;
            anyEnterable = true;
            if (_reach[x, y]) anyReached = true;
        }

        private static bool WaterMode()
        {
            try { return MainControl.getDataControl()?.getParty()?.canTraverseWater() ?? false; }
            catch { return false; }
        }

        /// <summary>Can the party occupy this tile at all, under the current
        /// locomotion mode — NavigationTools' node passability plus the door
        /// divergence.</summary>
        private static bool Enterable(MapTile t)
        {
            if (t.isPassable())
            {
                if (!_waterMode && t.isWater() && !t.hasVehicle()) return false;
                return true;
            }
            var door = t.getPropOrGuestProp() as PropDoor;
            return door != null && !door.isLocked() && !door.isHidden();
        }

        private static bool EnsureFresh(object mapObj, MapTileGrid grid)
        {
            bool mode = WaterMode();
            if (ReferenceEquals(_mapRef.Target, mapObj) && !_dirty
                && mode == _waterMode && _reach != null
                && PartyXY(mapObj, out int hx, out int hy)
                && hx >= 0 && hy >= 0 && hx < _w && hy < _h && _reach[hx, hy])
                return true;

            _waterMode = mode;
            _w = grid.getMapTileWidth();
            _h = grid.getMapTileHeight();
            if (_w <= 0 || _h <= 0) return false;
            if (!PartyXY(mapObj, out int px, out int py)
                || px < 0 || py < 0 || px >= _w || py >= _h) return false;

            var reach = new bool[_w, _h];
            var queue = new Queue<int>();
            reach[px, py] = true; // start forced, the pathfinder's own rule
            queue.Enqueue(px * _h + py);

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int x = idx / _h, y = idx % _h;
                MapTile from = grid.getTile(x, y);
                bool fromWater = from != null && from.isWater();
                Step(grid, reach, queue, fromWater, x + 1, y);
                Step(grid, reach, queue, fromWater, x - 1, y);
                Step(grid, reach, queue, fromWater, x, y + 1);
                Step(grid, reach, queue, fromWater, x, y - 1);
            }

            _reach = reach;
            _mapRef.Target = mapObj;
            _dirty = false;
            return true;
        }

        private static void Step(MapTileGrid grid, bool[,] reach, Queue<int> queue,
            bool fromWater, int x, int y)
        {
            if (x < 0 || y < 0 || x >= _w || y >= _h || reach[x, y]) return;
            MapTile t = grid.getTile(x, y);
            if (t == null || !Enterable(t)) return;
            // The pathfinder's directed edge rule verbatim: from land, water
            // is enterable only where a vehicle sits; from water, anything.
            if (!fromWater && t.isWater() && !t.hasVehicle()) return;
            reach[x, y] = true;
            queue.Enqueue(x * _h + y);
        }

        private static bool PartyXY(object mapObj, out int x, out int y)
        {
            x = 0; y = 0;
            try
            {
                if (Seams.Map_getXPos == null || Seams.Map_getYPos == null) return false;
                x = (int)Seams.Map_getXPos.Invoke(mapObj, null);
                y = (int)Seams.Map_getYPos.Invoke(mapObj, null);
                return true;
            }
            catch { return false; }
        }
    }
}

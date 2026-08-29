using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace SkaldAccessibility
{
    /// <summary>
    /// The tile-art transcode (owner go 2026-08-29, first pass — audit
    /// pending): the classification job's vocabulary (docs/tile-art,
    /// 2026-08-24) compiled into a (sheet, subImage) → noun table, spoken
    /// where the overland ladder used to fall to bare flag words. OVERLAND
    /// ONLY by owner ruling — combat keeps the clear/blocked fallback for
    /// fluidity, enforced by simply not wiring this into CombatCursor's
    /// ladder.
    ///
    /// Primacy (owner ruling 2026-08-29): anything the game labels wins by
    /// ladder order — occupants, ships, named props (with verb), items,
    /// corpses, designer info-tile names all sit ABOVE this rung and are
    /// untouched. The transcode replaces only the flag-word tail, which is
    /// also what a game-labeled-but-NAMELESS interactive (the discrepancy
    /// census class: braziers, the spider-cave entrance) falls through to —
    /// those now speak the terrain noun under them instead of "Blocked".
    /// Per-object exceptions (a noun replacing a name where meaning and
    /// brevity both hold) are owner calls added as they surface in play.
    ///
    /// Liveness by construction: only the VOCABULARY is static. The layer
    /// stack is read live from the tile's own render buffer
    /// (textureBuffer.imageLayers — the array the renderer draws from) at
    /// every utterance; there is no per-tile cache to go stale. The water
    /// family (no sprite sheet) resolves by the tile's animationPath as the
    /// ground-most fallback.
    ///
    /// Precedence within a tile (consumer-side rule the job deferred):
    /// layers walk TOP-DOWN (walls before grounds — construction order in
    /// MapTile's constructor), first speakable label wins; mute cells
    /// (invisible art, baked shadows) fall through to the layer beneath.
    /// </summary>
    internal static class TileArtTable
    {
        private sealed class Cell
        {
            public string Label;   // null = mute (fall through)
            public bool Wall;      // layer-role wall: noun self-evidently blocks
        }

        private sealed class Sheet
        {
            public Cell Default;
            public Dictionary<int, Cell> Exceptions;
        }

        private static Dictionary<string, Sheet> _sheets;
        private static Dictionary<string, Cell> _anims;
        private static bool _parseFailed;

        // ---- Config ----
        private static ConfigEntry<bool> _cfgNouns;
        private static ConfigEntry<bool> _cfgCoords;

        internal static void BindConfig(ConfigFile config)
        {
            _cfgNouns = config.Bind("Tiles", "ArtNouns", true,
                "Speak the tile-art vocabulary (\"stone wall\", \"surf\", \"flagstones\") where the "
                + "overland tile ladder used to say only Open/Blocked/Water. First-pass labels "
                + "(2026-08-24 classification job), sighted audit pending. Combat is unaffected "
                + "either way (owner ruling: combat keeps the terse flag words). Off restores the "
                + "flag words overland too.");
            _cfgCoords = config.Bind("Tiles", "Coordinates", true,
                "Append the tile's map coordinates (\"X 42, Y 70.\") to cursor tile landings, both "
                + "worlds — the same values the game's own X Pos./Y Pos. panel shows.");
        }

        internal static bool NounsOn => _cfgNouns == null || _cfgNouns.Value;

        /// <summary>", X 42, Y 70." — appended to tile landings ahead of any
        /// trailing browse counter (positional counts trail). Empty when off.</summary>
        internal static string CoordTail(int x, int y)
        {
            if (_cfgCoords != null && !_cfgCoords.Value) return "";
            return $" X {x}, Y {y}.";
        }

        /// <summary>The transcode noun for a spotted tile, or null (unknown
        /// sheet, all layers mute, config off, seams missing) — null means
        /// the caller's flag-word tail stands. wallish reports the winning
        /// label's layer role for the blocked-qualifier rule.</summary>
        internal static string LabelFor(object tile, out bool wallish)
        {
            wallish = false;
            if (tile == null || !NounsOn) return null;
            if (!EnsureLoaded()) return null;
            try
            {
                object tb = Seams.MapTile_textureBuffer?.GetValue(tile);
                var arr = tb == null ? null : Seams.TextureBuffer_imageLayers?.GetValue(tb) as Array;
                if (arr != null && Seams.ImageLayer_path != null && Seams.ImageLayer_subImage != null)
                {
                    for (int i = arr.Length - 1; i >= 0; i--)
                    {
                        object layer = arr.GetValue(i);
                        if (layer == null) continue;
                        string path = Seams.ImageLayer_path.GetValue(layer) as string;
                        if (string.IsNullOrEmpty(path)) continue;
                        if (!_sheets.TryGetValue(path, out Sheet sheet)) continue;
                        int sub = Convert.ToInt32(Seams.ImageLayer_subImage.GetValue(layer));
                        Cell cell;
                        if (!sheet.Exceptions.TryGetValue(sub, out cell)) cell = sheet.Default;
                        if (cell == null || cell.Label == null) continue;   // mute → next layer down
                        wallish = cell.Wall;
                        return cell.Label;
                    }
                }

                // Ground-most fallback: the water family has no sprite sheet —
                // its identity is the animation key (ocean/shallows/surf/shore/sewage).
                string anim = Seams.MapTile_animationPath?.GetValue(tile) as string;
                if (!string.IsNullOrEmpty(anim) && _anims.TryGetValue(anim, out Cell a))
                {
                    wallish = a.Wall;
                    return a.Label;
                }
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("TileArt.LabelFor", ex.ToString());
            }
            return null;
        }

        // ---- TSV parse (pure string work — safe at any boot phase) ----

        private static bool EnsureLoaded()
        {
            if (_sheets != null) return true;
            if (_parseFailed) return false;
            try
            {
                var sheets = new Dictionary<string, Sheet>(StringComparer.Ordinal);
                var anims = new Dictionary<string, Cell>(StringComparer.Ordinal);
                Sheet current = null;
                foreach (string raw in TileArtData.Tsv.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    string[] f = line.Split('\t');
                    switch (f[0])
                    {
                        case "S":   // S path defaultLabel role
                            current = new Sheet
                            {
                                Default = MakeCell(f[2], f[3]),
                                Exceptions = new Dictionary<int, Cell>(),
                            };
                            sheets[f[1]] = current;
                            break;
                        case "G":   // G label role subSpec — attaches to the last S
                            if (current == null) break;
                            Cell cell = MakeCell(f[1], f[2]);
                            foreach (string part in f[3].Split(','))
                            {
                                int dash = part.IndexOf('-');
                                if (dash > 0)
                                {
                                    int lo = int.Parse(part.Substring(0, dash));
                                    int hi = int.Parse(part.Substring(dash + 1));
                                    for (int s = lo; s <= hi; s++) current.Exceptions[s] = cell;
                                }
                                else
                                {
                                    current.Exceptions[int.Parse(part)] = cell;
                                }
                            }
                            break;
                        case "A":   // A animationName label role
                            anims[f[1]] = MakeCell(f[2], f[3]);
                            break;
                    }
                }
                _sheets = sheets;
                _anims = anims;
                Plugin.Logger?.LogInfo(
                    $"[TileArt] vocabulary loaded: {sheets.Count} sheets, {anims.Count} animation keys.");
                return true;
            }
            catch (Exception ex)
            {
                _parseFailed = true;
                Plugin.Logger?.LogError("[TileArt] table parse failed — flag words stand: " + ex);
                return false;
            }
        }

        private static Cell MakeCell(string label, string role)
        {
            return new Cell
            {
                Label = label == "-" ? null : label,
                Wall = role == "w",
            };
        }
    }
}

using HarmonyLib;
using System;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// T light toggle spoken (owner request 2026-08-29): Inventory.toggleLight
    /// is the one choke — overland T and combat-planning T both route here via
    /// Party.toggleLightOnOff. Lit/doused is the game's own authoritative flag
    /// (getCurrentLight != null) read before and after; a no-op press (no
    /// lantern in the party) stays the game's own popup voice. The line names
    /// the actual light item ("Lantern lit." / "Torch doused.").
    /// </summary>
    [HarmonyPatch(typeof(Inventory), "toggleLight")]
    public static class LightTogglePatch
    {
        internal struct LightState
        {
            public bool Lit;
            public string Name;
        }

        static void Prefix(Character user, out LightState __state)
        {
            __state = default;
            try
            {
                var light = user?.getCurrentLight();
                __state.Lit = light != null;
                __state.Name = light == null ? null : TextCleaner.CleanText(light.getName());
            }
            catch { }
        }

        static void Postfix(Character user, LightState __state)
        {
            try
            {
                var light = user?.getCurrentLight();
                bool lit = light != null;
                if (lit == __state.Lit) return; // no-op press — the game's popup speaks

                string name = lit ? TextCleaner.CleanText(light.getName()) : __state.Name;
                if (string.IsNullOrWhiteSpace(name)) name = "Light";
                Scaffold.SpeechService.SayQueued(lit ? $"{name} lit." : $"{name} doused.", "Light");
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("LightToggle", ex.Message);
            }
        }
    }

    /// <summary>
    /// Combat deployment feedback (owner request 2026-09-01, closing his own
    /// "unvoiced selection status" flag): placeCharacterAtMouseTile is the
    /// one click handler for the placement phase, branching into two silent
    /// outcomes — clicking a party member makes them the actively deploying
    /// unit (Party.setCurrentPC), clicking a legal placement tile moves the
    /// current unit there. Both now speak: "Leon selected." on the swap,
    /// "Leon placed. X 12, Y 40." on the move (the coordinate tail is the
    /// tile-landing grammar, riding the same Tiles.Coordinates config).
    /// Illegal clicks stay silent — the game refuses them silently too.
    /// </summary>
    [HarmonyPatch(typeof(CombatEncounter), "placeCharacterAtMouseTile")]
    public static class PlacementClickPatch
    {
        public struct PlaceState
        {
            public object PC;
            public int X, Y;
        }

        static void Prefix(Character character, out PlaceState __state)
        {
            __state = default;
            try
            {
                __state.PC = MainControl.getDataControl()?.getCurrentPC();
                __state.X = character != null ? character.getTileX() : int.MinValue;
                __state.Y = character != null ? character.getTileY() : int.MinValue;
            }
            catch { }
        }

        static void Postfix(Character character, PlaceState __state)
        {
            try
            {
                if (GameStateTracker.CurrentMode != GameMode.CombatPlacement) return;

                object pcNow = MainControl.getDataControl()?.getCurrentPC();
                if (pcNow != null && !ReferenceEquals(pcNow, __state.PC))
                {
                    string name = TextCleaner.CleanText((pcNow as Character)?.getName() ?? "");
                    if (string.IsNullOrWhiteSpace(name)) name = "Character";
                    Scaffold.SpeechService.Say($"{name} selected.", "Nav");
                    return;
                }

                if (character != null
                    && (character.getTileX() != __state.X || character.getTileY() != __state.Y))
                {
                    string name = TextCleaner.CleanText(character.getName() ?? "");
                    if (string.IsNullOrWhiteSpace(name)) name = "Character";
                    Scaffold.SpeechService.Say(
                        $"{name} placed.{TileArtTable.CoordTail(character.getTileX(), character.getTileY())}",
                        "Nav");
                }
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("Placement", ex.Message);
            }
        }
    }

    /// <summary>
    /// Placement WASD nudges (owner extension 2026-09-01, same package):
    /// placeCharacterAtAdjacentTile moves the deploying unit one step — onto
    /// an open placement tile, or TRADING PLACES with a fellow party member.
    /// "Moved to X 12, Y 40." / "Swapped with Kat. X 12, Y 40." — swap
    /// detected by the vacated tile now holding a party member. A refused
    /// nudge (outside the zone) changes nothing and stays silent.
    /// </summary>
    [HarmonyPatch(typeof(CombatEncounter), "placeCharacterAtAdjacentTile")]
    public static class PlacementNudgePatch
    {
        static void Prefix(Character character, out PlacementClickPatch.PlaceState __state)
        {
            __state = default;
            try
            {
                __state.X = character != null ? character.getTileX() : int.MinValue;
                __state.Y = character != null ? character.getTileY() : int.MinValue;
            }
            catch { }
        }

        static void Postfix(Character character, PlacementClickPatch.PlaceState __state)
        {
            try
            {
                if (GameStateTracker.CurrentMode != GameMode.CombatPlacement) return;
                if (character == null) return;
                int nx = character.getTileX(), ny = character.getTileY();
                if (nx == __state.X && ny == __state.Y) return; // refused — the game's silent rule

                string swapped = null;
                try
                {
                    var map = MainControl.getDataControl()?.currentMap;
                    object old = map == null ? null
                        : Seams.Map_getTile?.Invoke(map, new object[] { __state.X, __state.Y });
                    var occ = (old as MapTile)?.getCharacter();
                    if (occ != null && occ.isPC()) swapped = TextCleaner.CleanText(occ.getName() ?? "");
                }
                catch { }

                // Placement nudges are always key-driven single steps — speak
                // per step, swaps and moves alike (owner ruling 2026-09-01).
                string coords = TileArtTable.CoordTail(nx, ny);
                Scaffold.SpeechService.Say(!string.IsNullOrWhiteSpace(swapped)
                    ? $"Swapped with {swapped}.{coords}"
                    : coords.Length > 0 ? $"Moved to{coords}" : "Moved.", "Nav");
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("Placement", ex.Message);
            }
        }
    }

    /// <summary>
    /// Combat step receipts (owner request 2026-09-01): the planning-phase
    /// WASD step commits through CombatEncounter.moveCharacter — a plain
    /// step onto an open tile spoke nothing. "Moved to X 12, Y 40." for the
    /// player's own steps only: gated to CombatPlanning (the same method
    /// serves AI turns elsewhere), a PC mover, an actual tile change (a
    /// blocked bump waits instead — the game's own feedback stands), and an
    /// UNOCCUPIED target (ally swaps and enemy bumps already speak through
    /// their own channels — owner: "swaps already do").
    /// </summary>
    [HarmonyPatch(typeof(CombatEncounter), "moveCharacter")]
    public static class CombatStepPatch
    {
        public struct StepState
        {
            public Character Mover;
            public int X, Y;
            public bool TargetOccupied;
        }

        static void Prefix(CombatEncounter __instance, int x, int y, out StepState __state)
        {
            __state = default;
            try
            {
                var ch = __instance.getCurrentCharacter();
                __state.Mover = ch;
                if (ch == null) return;
                __state.X = ch.getTileX();
                __state.Y = ch.getTileY();
                var map = MainControl.getDataControl()?.currentMap;
                object t = map == null ? null
                    : Seams.Map_getTile?.Invoke(map, new object[] { __state.X + x, __state.Y + y });
                __state.TargetOccupied = (t as MapTile)?.getCharacter() != null;
            }
            catch { }
        }

        static void Postfix(StepState __state)
        {
            try
            {
                if (GameStateTracker.CurrentMode != GameMode.CombatPlanning) return;
                var ch = __state.Mover;
                if (ch == null || !ch.isPC()) return;
                if (__state.TargetOccupied) return;
                int nx = ch.getTileX(), ny = ch.getTileY();
                if (nx == __state.X && ny == __state.Y) return;
                string coords = TileArtTable.CoordTail(nx, ny);
                // WASD step = speak now, per step (owner ruling: the player's
                // own pace is the pacing); a clicked course accumulates at
                // the Pump and speaks once where the walk stops, gated on the
                // game's own moveAlongCombatPath flag.
                string line = coords.Length > 0 ? $"Moved to{coords}" : "Moved.";
                bool walking = false;
                try { walking = ch.moveAlongCombatPath; } catch { }
                if (walking) Pump.NoteMoveReceipt(line, ch);
                else Scaffold.SpeechService.Say(line, "Nav");
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("CombatStep", ex.Message);
            }
        }
    }

    /// <summary>
    /// Stealth toggle (owner request 2026-08-29): DataControl.hide toggles the
    /// current PC's hidden flag then spends a round via passRound, whose
    /// generic "You wait a short while." strip line is what used to speak.
    /// The note lets the Pump swap exactly that line for "Entered stealth." /
    /// "Left stealth." from the game's own isHidden flag; genuine waits keep
    /// their line.
    /// </summary>
    [HarmonyPatch(typeof(DataControl), "hide")]
    public static class HidePatch
    {
        static void Postfix(DataControl __instance)
        {
            try
            {
                var pc = __instance.getCurrentPC();
                if (pc == null) return;
                // Spoken here, from the game's own flag — never anchored to
                // the strip line, whose dedup can swallow a repeat (the
                // owner-caught "left stealth goes quiet" bug). The note only
                // drops that misleading wait line at the drain.
                Scaffold.SpeechService.SayQueued(
                    pc.isHidden() ? "Entered stealth." : "Left stealth.", "Stealth");
                Pump.NoteStealthToggle();
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("Hide", ex.Message);
            }
        }
    }

    /// <summary>
    /// Character-change vitals (owner request 2026-08-29): DataControl.changePC
    /// is the one choke for the Next Character cycle (StateBase quick-button,
    /// the popup carousel path). The line — "8 of 10 vitality" + wounds and
    /// conditions only when nonzero (owner: zero conditions, no string at
    /// all) — notes at the Pump and drains right after DrainContent, queuing
    /// behind the game's own "X is now leading the party." strip line.
    /// index == 0 mirrors the game's own no-change guard.
    /// </summary>
    [HarmonyPatch(typeof(DataControl), "changePC")]
    public static class ChangePCPatch
    {
        static void Postfix(DataControl __instance, int index)
        {
            try
            {
                if (index == 0) return;
                object pc = __instance.getCurrentPC();
                if (pc == null) return;
                string line = ComposeVitals(pc);
                if (line != null) Pump.NotePCVitals(line);
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("ChangePC", ex.Message);
            }
        }

        private static string ComposeVitals(object ch)
        {
            int vit = I(Seams.Character_getVitality, ch);
            if (vit < 0) return null;
            int maxVit = I(Seams.Character_getMaxVitality, ch);
            string line = maxVit > 0 ? $"{vit} of {maxVit} vitality" : $"{vit} vitality";

            int wounds = I(Seams.Character_getWounds, ch);
            if (wounds > 0) line += $", {wounds} {(wounds == 1 ? "wound" : "wounds")}";

            int cond = CombatantDocument.ConditionCount(ch);
            if (cond > 0) line += $", {cond} {(cond == 1 ? "condition" : "conditions")}";

            return line + ".";
        }

        private static int I(System.Reflection.MethodInfo m, object ch)
        {
            try { return m == null ? -1 : (int)m.Invoke(ch, null); }
            catch { return -1; }
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace SkaldAccessibility
{
    /// <summary>
    /// F1 contextual key table (owner design 2026-08-29, replacing the F1
    /// repeat key — repeat retired outright, owner ruling same day): a
    /// two-column virtual table — key, function — with its rows resolved per
    /// surface, browsed like the mod's other overlays. Up/Down = rows,
    /// Left/Right = column scope (keys only / functions only), F1, Escape, or
    /// Backspace closes announced, and ANY other key closes silently and is
    /// CONSUMED — never falls through. (The K-list's close-and-fall-through
    /// discipline is same-handler and race-free; across the native feed the
    /// same frame's read order is not guaranteed, so this table eats the
    /// press instead. One press to close, the next press acts.)
    ///
    /// While engaged the game is blind: every SkaldIO binding read is
    /// swallowed (SkaldIOPatch chokes) and the whole controller feed suspends
    /// (ControllerFeedPatch), both through the one Engaged predicate, which
    /// carries a one-frame eat tail after close (the ReviewLayer idiom).
    /// Static rows only — the table drives nothing, so it is safe under
    /// popups and grids and simply describes them instead.
    ///
    /// Row wording: key and function only, terse; per-screen edge cases ride
    /// their key's row in that screen's table (owner: "brevity is key").
    /// The mod's own overlays get their own tables — the POI list and
    /// combatant overview rows teach that Z still acts on the highlighted
    /// entry (owner's why, 2026-08-29).
    /// </summary>
    public static class KeyTable
    {
        private static bool _active;
        private static int _eatUntilFrame = -1;
        private static int _row;   // -1 = just opened, no row landed yet
        private static int _col;   // -1 = both columns, 0 = key, 1 = function
        private static string _title;
        private static string _stateAtOpen;
        private static List<KeyValuePair<string, string>> _rows;

        private static BepInEx.Configuration.ConfigEntry<bool> _enabled;

        public static void BindConfig(BepInEx.Configuration.ConfigFile config)
        {
            _enabled = config.Bind("Tables", "KeyTable", true,
                "F1 opens a browsable key table for the current screen (Up/Down rows, Left/Right columns).");
        }

        public static bool Active => _active;

        /// <summary>True while open or in the one-frame eat tail after a
        /// close — the game must not see any key (SkaldIO chokes) and the
        /// controller feed must not fire (ControllerFeedPatch).</summary>
        public static bool Engaged => _active || Time.frameCount <= _eatUntilFrame;

        /// <summary>Choke-point predicate (SkaldIOPatch): while engaged the
        /// game sees no bound key at all.</summary>
        public static bool ShouldSwallowKey(KeyCode key) => Engaged;

        /// <summary>Called from InputHandler after the slash stop, before the
        /// review layer. True = press consumed.</summary>
        public static bool ProcessInput()
        {
            if (!_active)
            {
                if (Input.GetKeyDown(KeyCode.F1))
                {
                    if (_enabled != null && !_enabled.Value) return false;
                    // Review owns its moment — F1 stays inert rather than
                    // stacking a second modal browse over the buffer.
                    if (ReviewLayer.Active) return true;
                    Open();
                    return true;
                }
                return false;
            }

            // A state transition under the table makes its rows stale — the
            // standing overlay rule, self-checked here so no Pump wiring is
            // needed (the game can transition without a keypress).
            if (GameStateTracker.CurrentStateName != _stateAtOpen)
            {
                CloseSilent();
                return false;
            }

            if (Input.GetKeyDown(KeyCode.F1) || Input.GetKeyDown(KeyCode.Escape)
                || Input.GetKeyDown(KeyCode.Backspace))
            {
                Close();
                return true;
            }
            if (Input.GetKeyDown(KeyCode.UpArrow)) { Step(-1); return true; }
            if (Input.GetKeyDown(KeyCode.DownArrow)) { Step(+1); return true; }
            if (Input.GetKeyDown(KeyCode.LeftArrow)) { SetColumn(0); return true; }
            if (Input.GetKeyDown(KeyCode.RightArrow)) { SetColumn(1); return true; }

            // Speech control stays live inside the table: slash already
            // stopped upstream; the brackets fall through to the history
            // handler at the bottom of InputHandler.
            if (Input.GetKeyDown(KeyCode.Slash)) return true;
            if (Input.GetKeyDown(KeyCode.LeftBracket) || Input.GetKeyDown(KeyCode.RightBracket))
                return false;

            // Anything else: close silently, eat the press.
            if (Input.anyKeyDown)
            {
                CloseSilent();
                return true;
            }
            return false;
        }

        // ---- Open / close ----

        private static void Open()
        {
            Resolve(out _title, out _rows);
            _row = -1;
            _col = -1;
            _stateAtOpen = GameStateTracker.CurrentStateName;
            _active = true;
            Scaffold.SpeechService.Say(
                $"Key table, {_title}. {_rows.Count} entries.", "KeyTable");
        }

        private static void Close()
        {
            _active = false;
            _eatUntilFrame = Time.frameCount + 1;
            Scaffold.SpeechService.Say("Key table closed.", "KeyTable");
        }

        private static void CloseSilent()
        {
            _active = false;
            _eatUntilFrame = Time.frameCount + 1;
        }

        // ---- Browse ----

        private static void Step(int dir)
        {
            if (_rows.Count == 0) return;
            int next = _row < 0 ? (dir > 0 ? 0 : _rows.Count - 1)
                                : Mathf.Clamp(_row + dir, 0, _rows.Count - 1);
            _row = next;
            SpeakRow();
        }

        private static void SetColumn(int col)
        {
            bool changed = _col != col;
            _col = col;
            if (_row < 0) _row = 0;
            if (changed)
                Scaffold.SpeechService.Say(
                    (col == 0 ? "Keys. " : "Functions. ") + Cell(), "KeyTable");
            else
                SpeakRow();
        }

        private static void SpeakRow()
            => Scaffold.SpeechService.Say($"{Cell()} {_row + 1} of {_rows.Count}.", "KeyTable");

        private static string Cell()
        {
            var r = _rows[_row];
            if (_col == 0) return r.Key + ".";
            if (_col == 1) return Capitalize(r.Value) + ".";
            return $"{r.Key}: {r.Value}.";
        }

        private static string Capitalize(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // ---- Context resolution ----
        // Priority: the mod's own overlays first (they own the keys while
        // open), then popups/grids (they own the moment), then the state
        // family via the mode classifier.

        private static void Resolve(out string title, out List<KeyValuePair<string, string>> rows)
        {
            rows = new List<KeyValuePair<string, string>>();

            if (OverlandCursor.ListOpen)
            {
                title = "points of interest list";
                Add(rows, "Up and Down", "entries");
                Add(rows, "Left and Right", "category pages");
                Add(rows, "Z", "act on the highlighted entry — the game's click on its tile; the list stays open");
                Add(rows, "V", "read the walking path to the highlighted entry");
                Add(rows, "W A S D", "walk; the list follows");
                Add(rows, "P", "cursor back to the party");
                Add(rows, "K or Escape", "close the list");
            }
            else if (CombatCursor.ListOpen)
            {
                title = "combatant overview";
                Add(rows, "Up and Down", "entries");
                Add(rows, "Left and Right", "tabs");
                Add(rows, "W A S D", "step your character — the overview stays up");
                Add(rows, "Z", "close and click the shown tile — the game's click still acts there");
                Add(rows, "K or Escape", "close the overview");
            }
            else if (PopupOpen())
            {
                title = "popup";
                Add(rows, "Arrows", "options");
                Add(rows, "Numbers", "choose an option");
                Add(rows, "Z", "confirm the highlighted option");
                Add(rows, "Escape", "cancel");
            }
            else if (Patches.GridNavigationPatch.GridActive())
            {
                title = "selector grid";
                Add(rows, "Arrows", "walk the grid");
                Add(rows, "Z", "confirm — uses the ability or item");
                Add(rows, "X", "description");
                Add(rows, "Escape", "cancel");
            }
            else
            {
                switch (GameStateTracker.CurrentMode)
                {
                    case GameMode.Overland:
                        title = "overland";
                        Add(rows, "W A S D", "walk");
                        Add(rows, "Arrows", "tile cursor");
                        Add(rows, "Z", "the game's click at the cursor: path, attack, interact");
                        Add(rows, "H", "hostiles scan");
                        Add(rows, "N", "neutrals scan");
                        Add(rows, "B", "loot scan");
                        Add(rows, "O", "objects scan");
                        Add(rows, "M", "exits scan");
                        Add(rows, "P", "cursor back to the party");
                        Add(rows, "K", "points of interest list");
                        Add(rows, "V", "read the walking path to the cursor");
                        Add(rows, "T", "light or douse your lantern");
                        Add(rows, "Period", "next character, with vitals");
                        Add(rows, "Comma", "inventory");
                        Add(rows, "C", "character sheet");
                        break;

                    case GameMode.Combat:
                    case GameMode.CombatPlanning:
                    case GameMode.CombatResolve:
                    case GameMode.CombatPlacement:
                        title = "combat";
                        Add(rows, "W A S D", "step your character, or the placement cursor");
                        Add(rows, "Arrows", "battlefield cursor");
                        Add(rows, "Z", "the game's click at the cursor: move, attack, target, place");
                        Add(rows, "I", "next in initiative");
                        Add(rows, "U", "previous in initiative");
                        Add(rows, "P", "center on the active unit");
                        Add(rows, "K", "combatant overview");
                        Add(rows, "H", "hostiles scan");
                        Add(rows, "N", "friendlies scan");
                        Add(rows, "T", "light or douse your lantern");
                        Add(rows, "Left and Right Control", "the game's ability-bar paging");
                        break;

                    case GameMode.Scene:
                        title = "dialogue";
                        Add(rows, "Arrows", "dialogue options");
                        Add(rows, "Numbers", "choose an option");
                        Add(rows, "Z", "choose the highlighted option");
                        Add(rows, "W", "up into the story text; S walks back down");
                        Add(rows, "Escape", "back");
                        break;

                    case GameMode.Inventory:
                    case GameMode.Trade:
                        title = GameStateTracker.CurrentMode == GameMode.Trade ? "trade" : "inventory";
                        Add(rows, "W and S", "sections");
                        Add(rows, "Arrows", "rows and cells" + (GameStateTracker.CurrentMode == GameMode.Trade
                            ? "; Left and Right cross between inventories at the edge" : ""));
                        Add(rows, "Z", "select; second press uses or equips");
                        Add(rows, "X", "select and read the description");
                        Add(rows, "Q and E", "party member");
                        Add(rows, "Escape", "back");
                        break;

                    case GameMode.LevelUp:
                        title = "feats";
                        Add(rows, "W and S", "sections");
                        Add(rows, "Arrows", "the feat tree");
                        Add(rows, "Z", "highlight; second press buys a rank");
                        Add(rows, "X", "highlight; second press refunds a staged rank");
                        Add(rows, "Escape", "back");
                        break;

                    case GameMode.Settings:
                        title = "settings";
                        Add(rows, "W and S", "sections");
                        Add(rows, "Up and Down", "rows");
                        Add(rows, "Left and Right", "change the value");
                        Add(rows, "Escape", "back");
                        break;

                    default:
                        title = "this screen";
                        Add(rows, "W and S", "sections — A and D too");
                        Add(rows, "Arrows", "rows and cells");
                        Add(rows, "Z", "activate");
                        Add(rows, "X", "description");
                        Add(rows, "Q and E", "party member or page, where offered");
                        Add(rows, "Numbers", "buttons");
                        Add(rows, "Escape", "back");
                        break;
                }
            }

            // The global tail, every context.
            Add(rows, "Slash", "stop speech");
            Add(rows, "R", "review mode");
            Add(rows, "Home, End, Page Up, Page Down", "read the current document");
            Add(rows, "Left and Right brackets", "speech history");
            Add(rows, "F1", "close this table");
        }

        private static bool PopupOpen()
        {
            try { return Seams.PopUpControl_getCurrentPopUp?.Invoke(null, null) != null; }
            catch { return false; }
        }

        private static void Add(List<KeyValuePair<string, string>> rows, string key, string fn)
            => rows.Add(new KeyValuePair<string, string>(key, fn));
    }
}

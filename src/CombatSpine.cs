using System;
using BepInEx.Configuration;

namespace SkaldAccessibility
{
    /// <summary>
    /// CP1 (combat-spec §6, owner rulings 2026-08-18): the combat turn spine.
    /// Drain-called from the Pump between DrainTravel and DrainCombatLog, so a
    /// turn boundary always precedes that turn's own log narration, and the
    /// native PrimaryHeader "Round N" line (content drain, earlier) precedes
    /// both — round-line-then-turn-line without a mod-spoken round anchor.
    ///
    /// Every surface here is a COMPOSED SURFACE (owner ruling): an ordered set
    /// of independently queued utterances, each fed by its own data source and
    /// each individually configurable — the composer's parts model in
    /// miniature; CP2 inherits the shape.
    ///
    /// Mechanics this design rides (combat-survey cites):
    ///  - Round rollover is SYNCHRONOUS (§4c.1): current==null never survives
    ///    to the drain — the diff sees turn A → turn B directly, and rounds
    ///    are diffed on the game's own private `turns` counter, never a mod
    ///    model (the game itself skips the rest of a round when the current
    ///    character dies mid-turn, §4b.8).
    ///  - The native primaryHeader carries "Round N" every combat frame
    ///    (CombatBaseState.setGUIData:132 → getTitle) and already speaks via
    ///    the content stream — the round anchor is ABSORBED by not duplicating
    ///    it here. The turns diff drives logging and the (default-off)
    ///    per-round order utterance only.
    ///  - Turn cues are never load-bearing for action attribution (§4c.9):
    ///    multi-transition frames settle to the end state and intermediate
    ///    turns are legitimately unseen.
    ///  - Economy is scoped to the CURRENT PC (§4c.7 — the game renders the
    ///    counter for no one else) and diffed at the drain; the decrementers
    ///    are never patched (Mono inline class).
    ///  - Deployment order: initiative is rolled at ENCOUNTER CREATION and the
    ///    initiative list RENDERS during placement (§4c.2, §4c.10) — the order
    ///    line at deploy-phase start is render parity, names only (owner
    ///    ruling).
    /// </summary>
    internal static class CombatSpine
    {
        // ---- Composed-surface config (owner ruling 2026-08-18: each cue
        //      utterance is its own toggle; defaults per the phrasing stop) ----
        private static ConfigEntry<bool> _cfgTurnNames;
        private static ConfigEntry<bool> _cfgTurnEconomy;
        private static ConfigEntry<bool> _cfgRoundOrder;
        private static ConfigEntry<bool> _cfgEconomyDeltas;
        private static ConfigEntry<bool> _cfgForecast;

        internal static void BindConfig(ConfigFile config)
        {
            _cfgTurnNames = config.Bind("Combat", "TurnNames", true,
                "Speak whose turn it is (\"Bjorn's turn.\") for every combatant.");
            _cfgTurnEconomy = config.Bind("Combat", "TurnEconomy", true,
                "After a player character's turn cue, speak their starting economy (\"3 moves, 1 attack.\") as its own utterance.");
            _cfgRoundOrder = config.Bind("Combat", "RoundOrder", false,
                "Speak the initiative order at the start of every round (it rarely changes; the order is always available on demand).");
            _cfgEconomyDeltas = config.Bind("Combat", "EconomyDeltas", true,
                "Speak remaining moves/attacks when they change during the current player character's turn.");
            _cfgForecast = config.Bind("Combat", "CostForecast", true,
                "Speak an ability's action cost when aiming begins (\"Ends turn.\" / \"Costs all moves.\").");
        }

        // ---- Forecast note (from ActionCounterPatch — the game's own
        //      per-frame counter update, (character, ability); latest wins) ----
        private static object _pendingCounterChar;
        private static object _pendingCounterAbility;
        private static bool _counterNoted;

        internal static void NoteActionCounter(object character, object ability)
        {
            _pendingCounterChar = character;
            _pendingCounterAbility = ability;
            _counterNoted = true;
        }

        // ---- Per-encounter baselines (keyed on the CombatEncounter object —
        //      Planning is a singleton across combats, survey hazard 2; the
        //      encounter reference is the per-combat identity, §4c.10) ----
        private static object _encounter;
        private static object _currentChar;
        private static int _turnsSeen = int.MinValue;
        private static int _lastMoves = int.MinValue;
        private static int _lastAttacks = int.MinValue;
        private static bool _orderAnnounced;
        private static object _forecastAbility;
        private static bool _forecastSeen;   // distinguishes "no note yet" from "ability is null"

        private static void Reset()
        {
            _encounter = null;
            _currentChar = null;
            _turnsSeen = int.MinValue;
            _lastMoves = _lastAttacks = int.MinValue;
            _orderAnnounced = false;
            _forecastAbility = null;
            _forecastSeen = false;
        }

        /// <summary>Called from Pump.Drain between DrainTravel and
        /// DrainCombatLog. All reads are live game truth at the settled clock.</summary>
        internal static void Drain()
        {
            bool counterNoted = _counterNoted;
            object counterChar = _pendingCounterChar;
            object counterAbility = _pendingCounterAbility;
            _counterNoted = false;
            _pendingCounterChar = _pendingCounterAbility = null;

            object enc = ActiveEncounter();
            if (enc == null)
            {
                if (_encounter != null) Reset();
                return;
            }
            if (!ReferenceEquals(enc, _encounter))
            {
                Reset();
                _encounter = enc;
            }

            object state = Pump.CurrentStateObject();

            // Deployment order (owner ruling: names only, automatic at deploy
            // phase start — render parity with the initiative list the
            // placement GUI draws, §4c.10). Once per encounter.
            if (!_orderAnnounced && state != null && Seams.CombatPlacementStateType != null
                && Seams.CombatPlacementStateType.IsInstanceOfType(state))
            {
                _orderAnnounced = true;
                string order = ComposeOrderLine(enc);
                if (order != null)
                {
                    Scaffold.SpeechService.SayQueued(order, "Turn");
                    Plugin.Logger?.LogInfo($"[CombatSpine] deploy order: {order}");
                }
            }

            // Round diff — the game's own counter (§4c.1). The spoken anchor
            // is the native primaryHeader "Round N" line (absorbed by not
            // duplicating); this diff logs the boundary and carries the
            // optional order utterance.
            int turns = ReadTurns(enc);
            if (turns != int.MinValue)
            {
                if (_turnsSeen != int.MinValue && turns != _turnsSeen)
                {
                    Plugin.Logger?.LogInfo($"[CombatSpine] round {_turnsSeen} -> {turns}");
                    if (_cfgRoundOrder != null && _cfgRoundOrder.Value)
                    {
                        string order = ComposeOrderLine(enc);
                        if (order != null) Scaffold.SpeechService.SayQueued(order, "Turn");
                    }
                }
                _turnsSeen = turns;
            }

            // Turn diff — reference identity on the game's own current
            // character. A change resets the economy baseline (boundary
            // suppression: the round-start refresh must never read as a
            // refund, §4c.7) and the delta stays silent this frame (the cue
            // carries the news).
            object cur = GetCurrentCharacter(enc);
            bool turnChanged = !ReferenceEquals(cur, _currentChar);
            if (turnChanged)
            {
                _currentChar = cur;
                _lastMoves = _lastAttacks = int.MinValue;
                if (cur != null)
                {
                    string name = NameOf(cur);
                    if (name != null && _cfgTurnNames != null && _cfgTurnNames.Value)
                        Scaffold.SpeechService.SayQueued($"{name}'s turn.", "Turn");
                    Plugin.Logger?.LogInfo($"[CombatSpine] turn: {name ?? "?"}");

                    ReadEconomy(cur, out int mv, out int atk);
                    if (mv != int.MinValue)
                    {
                        if (IsPC(cur) && _cfgTurnEconomy != null && _cfgTurnEconomy.Value)
                            Scaffold.SpeechService.SayQueued(EconomyPhrase(mv, atk), "Turn");
                        _lastMoves = mv;
                        _lastAttacks = atk;
                    }
                }
            }
            else if (cur != null && IsPC(cur))
            {
                // Economy delta (owner grammar: changed counters as bare
                // remainders, trailing the action). Both-counters-zero stays
                // silent — the turn is over by definition and the next turn
                // cue carries the news (the ruled same-frame suppression,
                // widened to the whole wind-down: resolve-phase waits mean
                // the turn passes frames later, and "0 moves, 0 attacks"
                // before "Goblin's turn." is exactly the ruled-out noise).
                ReadEconomy(cur, out int mv, out int atk);
                if (mv != int.MinValue)
                {
                    if (_lastMoves != int.MinValue && (mv != _lastMoves || atk != _lastAttacks))
                    {
                        bool wiped = mv == 0 && atk == 0;
                        if (wiped && Pump.StealBark("Disengages"))
                        {
                            // The disengage announcement (owner ruling): one
                            // composed line on BOTH paths, absorbing the
                            // game's own bark and log line — never two
                            // utterances for one event.
                            Pump.StealCombatLogContaining("isengage");
                            Scaffold.SpeechService.SayQueued("Disengaged, turn ended.", "Turn");
                            Plugin.Logger?.LogInfo("[CombatSpine] disengage composed");
                        }
                        else if (!wiped && _cfgEconomyDeltas != null && _cfgEconomyDeltas.Value)
                        {
                            string delta = DeltaPhrase(mv, atk, mv != _lastMoves, atk != _lastAttacks);
                            if (delta != null) Scaffold.SpeechService.SayQueued(delta, "Turn");
                        }
                    }
                    _lastMoves = mv;
                    _lastAttacks = atk;
                }
            }

            // Cost forecast — the pending ability from the game's own action
            // counter update (targeting states pass their aimed component;
            // planning passes null). Speaks once per distinct pending ability,
            // composed from getTimeCost + the flash rule the pips render
            // (§4c.7: FullRound blinks everything; MoveEquivalent blinks move
            // pips, the attack pip only when moves are already zero).
            if (counterNoted)
            {
                if (!_forecastSeen || !ReferenceEquals(counterAbility, _forecastAbility))
                {
                    _forecastSeen = true;
                    _forecastAbility = counterAbility;
                    if (counterAbility != null && _cfgForecast != null && _cfgForecast.Value)
                    {
                        string cost = ForecastPhrase(counterAbility, counterChar);
                        if (cost != null)
                        {
                            Scaffold.SpeechService.SayQueued(cost, "Forecast");
                            Plugin.Logger?.LogInfo($"[CombatSpine] forecast: {cost}");
                        }
                    }
                }
            }
        }

        // ---- Reads (all reflection through the Seams registry, null-gated) ----

        private static object ActiveEncounter()
        {
            try
            {
                if (Seams.MainControl_getDataControl == null
                    || Seams.DataControl_getCombatEncounter == null
                    || Seams.DataControl_isCombatActive == null) return null;
                object dc = Seams.MainControl_getDataControl.Invoke(null, null);
                if (dc == null) return null;
                if (!(bool)Seams.DataControl_isCombatActive.Invoke(dc, null)) return null;
                return Seams.DataControl_getCombatEncounter.Invoke(dc, null);
            }
            catch { return null; }
        }

        private static object GetCurrentCharacter(object enc)
        {
            try
            {
                if (Seams.CombatEncounter_getCurrentCharacter == null) return null;
                return Seams.CombatEncounter_getCurrentCharacter.Invoke(enc, null);
            }
            catch { return null; }
        }

        private static int ReadTurns(object enc)
        {
            try
            {
                if (Seams.CombatEncounter_turns == null) return int.MinValue;
                return Convert.ToInt32(Seams.CombatEncounter_turns.GetValue(enc));
            }
            catch { return int.MinValue; }
        }

        private static void ReadEconomy(object character, out int moves, out int attacks)
        {
            moves = attacks = int.MinValue;
            try
            {
                if (Seams.Character_getRemainingCombatMoves == null
                    || Seams.Character_getRemainingAttacks == null) return;
                moves = (int)Seams.Character_getRemainingCombatMoves.Invoke(character, null);
                attacks = (int)Seams.Character_getRemainingAttacks.Invoke(character, null);
            }
            catch { moves = attacks = int.MinValue; }
        }

        private static bool IsPC(object character)
        {
            try
            {
                return Seams.Character_isPC != null
                    && (bool)Seams.Character_isPC.Invoke(character, null);
            }
            catch { return false; }
        }

        private static string NameOf(object character)
        {
            try
            {
                string raw = Seams.SkaldBaseObject_getName?.Invoke(character, null) as string;
                if (string.IsNullOrWhiteSpace(raw)) return null;
                string cleaned = Patches.TextCleaner.CleanText(raw);
                return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
            }
            catch { return null; }
        }

        /// <summary>"Order: Bjorn, Goblin, Wolf." — full names (the Character
        /// binding; the UI truncates at 9 chars) in the game's own sorted
        /// order, names only (owner ruling — rolls are verbose-tier).</summary>
        private static string ComposeOrderLine(object enc)
        {
            try
            {
                if (Seams.CombatEncounter_initiativeList == null
                    || Seams.InitiativeList_getInitiativeList == null) return null;
                object il = Seams.CombatEncounter_initiativeList.GetValue(enc);
                if (il == null) return null;
                var list = Seams.InitiativeList_getInitiativeList.Invoke(il, null)
                    as System.Collections.IEnumerable;
                if (list == null) return null;
                var names = new System.Collections.Generic.List<string>();
                foreach (object c in list)
                {
                    if (c == null) continue;
                    string n = NameOf(c);
                    if (n != null) names.Add(n);
                }
                if (names.Count == 0) return null;
                return "Order: " + string.Join(", ", names.ToArray()) + ".";
            }
            catch { return null; }
        }

        private static string EconomyPhrase(int moves, int attacks)
        {
            string mv = moves == 1 ? "1 move" : $"{moves} moves";
            string atk = attacks == 1 ? "1 attack" : $"{attacks} attacks";
            return $"{mv}, {atk}.";
        }

        private static string DeltaPhrase(int moves, int attacks, bool movesChanged, bool attacksChanged)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (movesChanged) parts.Add(moves == 1 ? "1 move" : $"{moves} moves");
            if (attacksChanged) parts.Add(attacks == 1 ? "1 attack" : $"{attacks} attacks");
            if (parts.Count == 0) return null;
            return string.Join(", ", parts.ToArray()) + ".";
        }

        /// <summary>Cost before the fact, turn-enders named not counted (owner
        /// grammar). MECHANICAL CORRECTION to the ruled example (surfaced in
        /// the CP1 report): MoveEquivalent spends ALL remaining moves, not one
        /// (Character.useItemInCombat:2757-2766 — clearCombatMovesButNotAttacks;
        /// whole turn when moves are already zero), exactly matching the pip
        /// flash rule. MultiRound is unimplemented in payTimeCost and costs
        /// nothing (§4b.12) — silent.</summary>
        private static string ForecastPhrase(object ability, object character)
        {
            try
            {
                if (Seams.AbilityUseable_getTimeCost == null) return null;
                object cost = Seams.AbilityUseable_getTimeCost.Invoke(ability, null);
                switch (cost?.ToString())
                {
                    case "FullRound":
                        return "Ends turn.";
                    case "MoveEquivalent":
                        int mv = int.MinValue;
                        if (character != null) ReadEconomy(character, out mv, out _);
                        if (mv == 0) return "Ends turn.";
                        if (mv == 1) return "Costs 1 move.";
                        if (mv > 1) return $"Costs all {mv} moves.";
                        return "Costs all moves.";
                    case "Free":
                        return "Free.";
                    default:
                        return null;
                }
            }
            catch { return null; }
        }
    }
}

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

        private static ConfigEntry<bool> _cfgAoETerse;

        internal static void BindConfig(ConfigFile config)
        {
            _cfgAoETerse = config.Bind("Combat", "AoECensusTerse", false,
                "Terse AoE census: keep the enemy/ally counts, drop the names (owner ruling 2026-08-18).");
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

        // ---- Row-shift note (CP4, from RowShiftPatch — the game's own
        //      Ctrl-pair ability-shift; latest wins) ----
        private static object _pendingRowShiftGui;
        internal static void NoteRowShift(object guiControl) => _pendingRowShiftGui = guiControl;

        // ---- AoE census state (CP4): the settled selection's member set,
        //      reference-keyed, diffed at the drain ----
        private static readonly System.Collections.Generic.HashSet<object> _censusMembers
            = new System.Collections.Generic.HashSet<object>(new RefCmp());
        private static bool _censusActive;

        // ---- Weapon-preference diff (CP4): the game's own isWeaponRanged
        //      flag on the current PC ----
        private static object _weaponDiffChar;
        private static bool _weaponWasRanged;

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

        // ---- Lettered identifier registry (CP2, owner ruling 2026-08-18).
        //      Reference-keyed; letters assign in order of ENTRY into the
        //      initiative order — never position within it — and are durable
        //      against every reshuffle (Hold, re-sorts, deaths): once
        //      assigned, never changed, never reused. A name-group of one
        //      speaks bare; when a summon creates the duplicate, the original
        //      takes its letter at that moment (one rename, once). Any
        //      attribution failure degrades to the game's own bare text —
        //      the structural insurance policy. ----
        private sealed class RefCmp : System.Collections.Generic.IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
        private static readonly RefCmp Cmp = new RefCmp();
        private static readonly System.Collections.Generic.List<object> _roster
            = new System.Collections.Generic.List<object>();                       // every combatant ever seen, entry order
        private static readonly System.Collections.Generic.Dictionary<object, int> _letterOf
            = new System.Collections.Generic.Dictionary<object, int>(new RefCmp()); // index within the name-group
        private static readonly System.Collections.Generic.Dictionary<object, string> _bareOf
            = new System.Collections.Generic.Dictionary<object, string>(new RefCmp());
        private static readonly System.Collections.Generic.Dictionary<string, int> _groupSize
            = new System.Collections.Generic.Dictionary<string, int>();

        // Per-member frame snapshots (HP/death/bark-count) — the object-first
        // attribution truth for the composer.
        private sealed class Snap { public int Hp; public bool Dead; public int Barks; }
        private static readonly System.Collections.Generic.Dictionary<object, Snap> _snaps
            = new System.Collections.Generic.Dictionary<object, Snap>(new RefCmp());

        // This frame's attributed facts, rebuilt every drain, consumed by the
        // narration pass (Pump.DrainCombatLog runs after Drain in the same
        // frame, so the deltas are always current).
        private static Composer.Frame _frame;

        // Tactical flash elements already seen (reference identity — the
        // list retains elements across frames while they flash).
        private static readonly System.Collections.Generic.HashSet<object> _seenTactical
            = new System.Collections.Generic.HashSet<object>(new RefCmp());

        private static void Reset()
        {
            _encounter = null;
            _currentChar = null;
            _turnsSeen = int.MinValue;
            _lastMoves = _lastAttacks = int.MinValue;
            _orderAnnounced = false;
            _forecastAbility = null;
            _forecastSeen = false;
            _roster.Clear();
            _letterOf.Clear();
            _bareOf.Clear();
            _groupSize.Clear();
            _snaps.Clear();
            _seenTactical.Clear();
            _frame = null;
            _pendingRowShiftGui = null;
            _censusMembers.Clear();
            _censusActive = false;
            _weaponDiffChar = null;
            _placePos.Clear();
            _placeTracking = false;
        }

        /// <summary>"Goblin B" / "Bjorn" — the uniform identifier (owner
        /// mandate: applied everywhere the mod speaks; the game's own strings
        /// carry no identifier and are never spoken raw when a name maps).</summary>
        internal static string DisplayNameOf(object character)
        {
            string bare = NameOf(character);
            if (bare == null) return null;
            int idx;
            int size;
            if (_letterOf.TryGetValue(character, out idx)
                && _groupSize.TryGetValue(bare, out size) && size > 1)
                return bare + " " + LetterFor(idx);
            return bare;
        }

        private static string LetterFor(int idx)
        {
            if (idx < 26) return ((char)('A' + idx)).ToString();
            return "Z" + (idx - 24); // 27th duplicate onward: Z2, Z3, ... (never expected)
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

            // Roster maintenance + per-member snapshots (CP2): register new
            // combatants in entry order (summons roll into initiative on
            // entry, so the initiative-list walk catches them), then diff
            // HP/death/bark-count per member into this frame's attributed
            // facts for the composer.
            BuildFrameFacts(enc);

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
                    string name = DisplayNameOf(cur);
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

            // CP5: deployment swap voicing — keyboard placement moves are
            // hard-gated to valid tiles and stay unvoiced like all WASD
            // movement (the cursor is the sensing tool); the SWAP side effect
            // (stepping onto a party member exchanges places, silently) is
            // the surprise that speaks.
            try { DrainPlacementSwap(state); } catch (Exception ex) { Scaffold.Log.Throttled("CombatSpine:swap", ex.Message); }

            // CP4 joins: Ctrl-row compose, AoE census, weapon-toggle diff.
            try { DrainRowShift(); } catch (Exception ex) { Scaffold.Log.Throttled("CombatSpine:row", ex.Message); }
            try { DrainAoECensus(state); } catch (Exception ex) { Scaffold.Log.Throttled("CombatSpine:aoe", ex.Message); }
            try { DrainWeaponToggle(cur); } catch (Exception ex) { Scaffold.Log.Throttled("CombatSpine:weapon", ex.Message); }

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

        // ---- CP2: frame facts + narration ----

        /// <summary>Register new combatants (entry order) and diff every
        /// known member's HP/death/bark-count into this frame's Composer
        /// facts. Runs every drain while an encounter is active.</summary>
        private static void BuildFrameFacts(object enc)
        {
            var frame = new Composer.Frame();
            try
            {
                // Registration walk: the current initiative list, in order.
                if (Seams.CombatEncounter_initiativeList != null
                    && Seams.InitiativeList_getInitiativeList != null)
                {
                    object il = Seams.CombatEncounter_initiativeList.GetValue(enc);
                    var list = il == null ? null
                        : Seams.InitiativeList_getInitiativeList.Invoke(il, null)
                            as System.Collections.IEnumerable;
                    if (list != null)
                    {
                        foreach (object c in list)
                        {
                            if (c == null || _letterOf.ContainsKey(c)) continue;
                            string bare = NameOf(c);
                            if (bare == null) continue;
                            int size;
                            _groupSize.TryGetValue(bare, out size);
                            _letterOf[c] = size;          // entry order within the name-group
                            _groupSize[bare] = size + 1;
                            _bareOf[c] = bare;
                            _roster.Add(c);
                        }
                    }
                }

                // Snapshot diffs for every member ever seen (the initiative
                // list drops the dead — our roster never does, so death
                // flips and posthumous events stay attributable).
                foreach (object c in _roster)
                {
                    int hp = ReadHpTotal(c);
                    bool dead = ReadIsDead(c);
                    int barkCount = ReadBarkCount(c);
                    Snap prev;
                    bool hadPrev = _snaps.TryGetValue(c, out prev);
                    frame.Roster.Add(new Composer.Member
                    {
                        Display = DisplayNameOf(c),
                        Bare = _bareOf[c],
                        HpDelta = hadPrev && hp != int.MinValue && prev.Hp != int.MinValue
                            ? hp - prev.Hp : 0,
                        BecameDead = hadPrev && !prev.Dead && dead,
                        BarkGrowth = hadPrev && barkCount >= 0 && prev.Barks >= 0
                            ? Math.Max(0, barkCount - prev.Barks) : 0,
                        IsPC = IsPC(c),
                    });
                    _snaps[c] = new Snap { Hp = hp, Dead = dead, Barks = barkCount };
                }

                // Attack context: the current character and its target.
                object cur = GetCurrentCharacter(enc);
                if (cur != null)
                {
                    frame.AttackerDisplay = DisplayNameOf(cur);
                    frame.AttackerBare = NameOf(cur);
                    frame.AttackerIsRanged = ReadBool(Seams.Character_isWeaponRanged, cur);
                    object tgt = Seams.Character_getTargetOpponent?.Invoke(cur, null);
                    if (tgt != null)
                    {
                        frame.TargetDisplay = DisplayNameOf(tgt);
                        frame.TargetBare = NameOf(tgt);
                        // Identity link for the kill-overrun trailer (review
                        // find 8): frame.Roster rows are built in _roster
                        // order, so the index carries the object identity.
                        for (int i = 0; i < _roster.Count; i++)
                            if (ReferenceEquals(_roster[i], tgt)) { frame.TargetRosterIndex = i; break; }
                    }
                }

                // Tactical flashes: a pure READ of the retained element list —
                // never consumed (the buffer gates turn pacing, §4c.11).
                ReadTacticalFlashes(frame);
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("CombatSpine:facts", ex.Message);
            }
            _frame = frame;
        }

        /// <summary>Called from Pump.DrainCombatLog with the frame's log
        /// shorts and the LIVE pending-bark list. Composes the narration,
        /// consumes matched barks in place (the caller speaks the remainder),
        /// and emits via the event path. Returns false when narration could
        /// not run (caller falls back to plain speech).</summary>
        internal static bool NarrateCombatFrame(
            System.Collections.Generic.List<string> shorts,
            System.Collections.Generic.List<string> barks)
        {
            var frame = _frame;
            if (frame == null || _encounter == null) return false;
            frame.EventShorts.Clear();
            frame.EventShorts.AddRange(shorts);
            frame.Barks.Clear();
            frame.Barks.AddRange(barks);

            var lines = Composer.ComposeCombatFrame(frame);
            frame.Tactical.Clear();   // spoken by the composer — never doubled by the orphan path

            // Reflect bark consumption back into the pump's pending list.
            barks.Clear();
            barks.AddRange(frame.Barks);

            EmitEventLines(lines);
            return true;
        }

        // ---- CP5: placement swap detection (positions of every roster PC,
        //      diffed per drain while the deploy screen is up) ----
        private static readonly System.Collections.Generic.Dictionary<object, long> _placePos
            = new System.Collections.Generic.Dictionary<object, long>(new RefCmp());
        private static bool _placeTracking;

        private static void DrainPlacementSwap(object state)
        {
            bool placing = state != null && Seams.CombatPlacementStateType != null
                && Seams.CombatPlacementStateType.IsInstanceOfType(state);
            if (!placing)
            {
                if (_placeTracking) { _placeTracking = false; _placePos.Clear(); }
                return;
            }
            var moved = new System.Collections.Generic.List<object>();
            foreach (object c in _roster)
            {
                if (!IsPC(c)) continue;
                long pos = ReadPackedPos(c);
                if (pos == long.MinValue) continue;
                long prev;
                if (_placeTracking && _placePos.TryGetValue(c, out prev) && prev != pos)
                    moved.Add(c);
                _placePos[c] = pos;
            }
            _placeTracking = true;
            // Exactly two PCs moving in one frame during placement IS the
            // swap (CombatEncounter.placeCharacterAtAdjacentTile:291-307 —
            // the only multi-mover path).
            if (moved.Count == 2)
            {
                object dc = null;
                try { dc = Seams.MainControl_getDataControl?.Invoke(null, null); } catch { }
                object placer = null;
                try { placer = dc == null ? null : Seams.DataControl_getCurrentPC?.Invoke(dc, null); } catch { }
                object other = ReferenceEquals(moved[0], placer) ? moved[1] : moved[0];
                string name = DisplayNameOf(other);
                if (name != null)
                    Scaffold.SpeechService.Say($"Swapped with {name}.", "Nav");
            }
        }

        private static long ReadPackedPos(object c)
        {
            try
            {
                object tile = Seams.Character_getMapTile?.Invoke(c, null);
                if (tile == null) return long.MinValue;
                int x = (int)Seams.MapTile_getTileX.Invoke(tile, null);
                int y = (int)Seams.MapTile_getTileY.Invoke(tile, null);
                return ((long)x << 32) | (uint)y;
            }
            catch { return long.MinValue; }
        }

        /// <summary>CP4: the Ctrl-row walk's terse anchor</summary> — "3: Maneuvers." —
        /// from the row's own settled index and the state's ButtonData
        /// hoverText (the unavailability variants carry their own words);
        /// the game's fuller description echo follows natively.</summary>
        private static void DrainRowShift()
        {
            object gui = _pendingRowShiftGui;
            if (gui == null) return;
            _pendingRowShiftGui = null;
            if (Seams.GUIControl_combatButtonRow == null || Seams.UICanvas_currentSelectedButton == null)
                return;
            object row = Seams.GUIControl_combatButtonRow.GetValue(gui);
            if (row == null) return;
            int idx = (int)Seams.UICanvas_currentSelectedButton.GetValue(row);
            if (idx < 0) return;
            string label = null;
            try
            {
                object state = Pump.CurrentStateObject();
                if (state != null && Seams.CombatPlanning_buttonOptions != null
                    && Seams.CombatPlanningStateType != null
                    && Seams.CombatPlanningStateType.IsInstanceOfType(state))
                {
                    var options = Seams.CombatPlanning_buttonOptions.GetValue(state) as System.Collections.IList;
                    if (options != null && idx < options.Count && options[idx] != null
                        && Seams.ButtonData_hoverText != null)
                    {
                        string raw = Seams.ButtonData_hoverText.GetValue(options[idx]) as string;
                        if (!string.IsNullOrWhiteSpace(raw))
                            label = Patches.TextCleaner.CleanText(raw);
                    }
                }
            }
            catch { }
            Scaffold.SpeechService.Say(
                string.IsNullOrWhiteSpace(label) ? $"{idx + 1}." : $"{idx + 1}: {label}.", "Nav");
        }

        /// <summary>CP4: the AoE census (owner wording) — the settled
        /// selection's members, diffed as a set, corpse-filtered, faction-
        /// split: "In AoE, 2 enemies: Goblin A, Goblin B. 1 ally: Leo."
        /// Terse config keeps the counts and drops the names. The game
        /// recomputes the selection every frame from the mouse tile; the
        /// drain reads it settled.</summary>
        private static void DrainAoECensus(object state)
        {
            bool targeting = state != null && Seams.CombatTargetingBaseType != null
                && Seams.CombatTargetingBaseType.IsInstanceOfType(state);
            if (!targeting)
            {
                if (_censusActive) { _censusActive = false; _censusMembers.Clear(); }
                return;
            }
            object actor = GetCurrentCharacter(_encounter);
            if (actor == null || Seams.CharacterComponentContainer_areaEffectSelection == null
                || Seams.EffectSelection_getAllCharactersInSelection == null) return;
            // The field lives on the Character's COMPONENT CONTAINERS, not the
            // Character (the shipped read threw per targeting frame — Opus log
            // review 2026-08-19, 3,790 misses). The game checks the maneuver
            // container first, then spells (Character.areaEffectSelectionContains).
            object sel = AreaSelectionOf(actor);
            if (sel == null) return;
            var members = Seams.EffectSelection_getAllCharactersInSelection.Invoke(sel, null)
                as System.Collections.IEnumerable;
            var live = new System.Collections.Generic.List<object>();
            if (members != null)
            {
                foreach (object m in members)
                {
                    if (m == null) continue;
                    try
                    {
                        // Corpse filter (A1 caveat: the selection never filters isDead).
                        if (Seams.Character_isDead != null && (bool)Seams.Character_isDead.Invoke(m, null))
                            continue;
                    }
                    catch { }
                    live.Add(m);
                }
            }
            bool changed = !_censusActive || live.Count != _censusMembers.Count;
            if (!changed)
                foreach (object m in live)
                    if (!_censusMembers.Contains(m)) { changed = true; break; }
            if (!changed) return;
            _censusActive = true;
            _censusMembers.Clear();
            foreach (object m in live) _censusMembers.Add(m);

            var enemies = new System.Collections.Generic.List<string>();
            var allies = new System.Collections.Generic.List<string>();
            foreach (object m in live)
            {
                string dn = DisplayNameOf(m) ?? "Someone";
                bool hostile = false;
                try { hostile = Seams.Character_isHostile != null && (bool)Seams.Character_isHostile.Invoke(m, null); }
                catch { }
                (hostile ? enemies : allies).Add(dn);
            }
            if (enemies.Count == 0 && allies.Count == 0)
            {
                Scaffold.SpeechService.Say("In AoE, no one.", "Nav");
                return;
            }
            bool terse = _cfgAoETerse != null && _cfgAoETerse.Value;
            string e = enemies.Count == 0 ? null
                : (enemies.Count == 1 ? "1 enemy" : $"{enemies.Count} enemies")
                  + (terse ? "" : ": " + string.Join(", ", enemies.ToArray()));
            string a = allies.Count == 0 ? null
                : (allies.Count == 1 ? "1 ally" : $"{allies.Count} allies")
                  + (terse ? "" : ": " + string.Join(", ", allies.ToArray()));
            string line = "In AoE, " + (e != null && a != null ? e + ". " + a : e ?? a) + ".";
            Scaffold.SpeechService.Say(line, "Nav");
        }

        /// <summary>The actor's live area selection, read from whichever of
        /// the two component containers holds one — maneuver first, then
        /// spells, the game's own precedence (Character.
        /// areaEffectSelectionContains). Null when nothing is being aimed.</summary>
        private static object AreaSelectionOf(object actor)
        {
            try
            {
                var field = Seams.CharacterComponentContainer_areaEffectSelection;
                if (Seams.Character_getAbilityManueverContainer != null)
                {
                    object cont = Seams.Character_getAbilityManueverContainer.Invoke(actor, null);
                    object sel = cont != null ? field.GetValue(cont) : null;
                    if (sel != null) return sel;
                }
                if (Seams.Character_getSpellContainer != null)
                {
                    object cont = Seams.Character_getSpellContainer.Invoke(actor, null);
                    return cont != null ? field.GetValue(cont) : null;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>CP4: the weapon-toggle join (T / right-stick click) — the
        /// game's own isWeaponRanged flag diffed on the current PC; speaks the
        /// worn-zone vocabulary ("Ranged: Longbow." / "Melee: Shortsword.").
        /// Baseline resets silently at turn changes.</summary>
        private static void DrainWeaponToggle(object cur)
        {
            if (cur == null || !IsPC(cur))
            {
                _weaponDiffChar = null;
                return;
            }
            bool ranged;
            try
            {
                if (Seams.Character_isWeaponRanged == null) return;
                ranged = (bool)Seams.Character_isWeaponRanged.Invoke(cur, null);
            }
            catch { return; }
            if (!ReferenceEquals(cur, _weaponDiffChar))
            {
                _weaponDiffChar = cur;
                _weaponWasRanged = ranged;
                return;
            }
            if (ranged == _weaponWasRanged) return;
            _weaponWasRanged = ranged;
            string name = null;
            try
            {
                var getter = Seams.Character_wornGetters != null && Seams.Character_wornGetters.Length > 1
                    ? (ranged ? Seams.Character_wornGetters[1] : Seams.Character_wornGetters[0]) : null;
                object weapon = getter?.Invoke(cur, null);
                if (weapon != null)
                {
                    string raw = Seams.SkaldBaseObject_getName?.Invoke(weapon, null) as string;
                    if (!string.IsNullOrWhiteSpace(raw)) name = Patches.TextCleaner.CleanText(raw);
                }
            }
            catch { }
            string mode = ranged ? "Ranged" : "Melee";
            Scaffold.SpeechService.Say(name == null ? $"{mode}." : $"{mode}: {name}.", "Nav");
        }

        /// <summary>Tactical flashes with no log event still speak (Cascade
        /// has no other channel) — called from Drain when no narration ran
        /// this frame; NarrateCombatFrame covers them otherwise.</summary>
        internal static void SpeakOrphanTactical()
        {
            var frame = _frame;
            if (frame == null || frame.Tactical.Count == 0) return;
            foreach (string t in frame.Tactical)
                Scaffold.SpeechService.SayQueuedEvent(Composer.EnsurePeriod(t), "CombatEvent");
            frame.Tactical.Clear();
        }

        private static void EmitEventLines(System.Collections.Generic.List<string> lines)
        {
            foreach (string line in lines)
                Scaffold.SpeechService.SayQueuedEvent(line, "CombatEvent");
        }

        private static void ReadTacticalFlashes(Composer.Frame frame)
        {
            try
            {
                if (Seams.HoverElementControl_tacticalTextList == null
                    || Seams.UICanvas_getElements == null
                    || Seams.UITextBlock_content == null) return;
                var list = Seams.HoverElementControl_tacticalTextList.GetValue(null)
                    as System.Collections.IEnumerable;
                if (list == null) return;
                var live = new System.Collections.Generic.List<object>();
                foreach (object el in list)
                {
                    if (el == null) continue;
                    live.Add(el);
                    if (_seenTactical.Contains(el)) continue;
                    _seenTactical.Add(el);
                    var kids = Seams.UICanvas_getElements.Invoke(el, null)
                        as System.Collections.IEnumerable;
                    if (kids == null) continue;
                    foreach (object kid in kids)
                    {
                        if (!(kid is UITextBlock)) continue;
                        string raw = Seams.UITextBlock_content.GetValue(kid) as string;
                        string cleaned = string.IsNullOrWhiteSpace(raw) ? null
                            : Patches.TextCleaner.CleanText(raw);
                        if (!string.IsNullOrWhiteSpace(cleaned))
                        {
                            frame.Tactical.Add(cleaned);
                            Plugin.Logger?.LogInfo($"[CombatSpine] tactical: {cleaned}");
                        }
                        break; // the header element is the first text block
                    }
                }
                // Prune records for elements the game has retired.
                _seenTactical.RemoveWhere(o => !live.Contains(o));
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("CombatSpine:tactical", ex.Message);
            }
        }

        private static int ReadHpTotal(object c)
        {
            try
            {
                if (Seams.Character_getVitality == null || Seams.Character_getWounds == null)
                    return int.MinValue;
                return (int)Seams.Character_getVitality.Invoke(c, null)
                     + (int)Seams.Character_getWounds.Invoke(c, null);
            }
            catch { return int.MinValue; }
        }

        private static bool ReadIsDead(object c)
        {
            try
            {
                return Seams.Character_isDead != null
                    && (bool)Seams.Character_isDead.Invoke(c, null);
            }
            catch { return false; }
        }

        private static int ReadBarkCount(object c)
        {
            try
            {
                if (Seams.Character_barkControl == null || Seams.BarkControl_barks == null)
                    return -1;
                // The FIELD, never the lazy getter: getBarkControl() would
                // force-create the control, and physicMovementComplete's
                // barkControl!=null clause would then evaluate target-bark
                // waits vanilla skips — observing via the getter would CHANGE
                // combat pacing (review find 1). A null field = never barked
                // = an honest count of zero.
                object control = Seams.Character_barkControl.GetValue(c);
                if (control == null) return 0;
                var barks = Seams.BarkControl_barks.GetValue(control) as System.Collections.ICollection;
                return barks?.Count ?? -1;
            }
            catch { return -1; }
        }

        private static bool ReadBool(System.Reflection.MethodInfo m, object target)
        {
            try { return m != null && (bool)m.Invoke(target, null); }
            catch { return false; }
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
                    string n = DisplayNameOf(c);
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

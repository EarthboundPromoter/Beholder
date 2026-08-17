using System;
using System.Collections;
using SkaldAccessibility.Patches;

namespace SkaldAccessibility
{
    /// <summary>
    /// Tracks the game's current mode/state for context-aware speech routing.
    /// Maps the game's StateControl/StateBase hierarchy to our GameMode enum.
    ///
    /// Architecture:
    ///   MainControl holds stateControl (StateControl) and guiControl (GUIControl*)
    ///   StateControl manages state transitions via activateState/jumpToState
    ///   Each state has a boundState (parent/return state) and callingState
    ///   States form a hierarchy: *BaseState classes are parents for sub-states
    ///
    /// State hierarchy (from decompilation):
    ///   OverlandBaseState → OverlandState (exploration)
    ///   CombatBaseState → CombatStartState, CombatPlacementState, CombatPlanningState,
    ///                     CombatResolveState, CombatOverState, CombatLogState
    ///   SceneBaseState → SceneState (dialogue/CYOA)
    ///   InventoryBaseState → InventoryGridState, TradeState
    ///   ListSheetBaseState → JournalState, JournalInfoState, AbilitiesState, SpellsState
    ///   CharacterBuilderBaseState → CharacterCreation*State (character creation)
    ///   SettingsBaseState → SettingsAudioState, SettingsGameplayState,
    ///                       SettingsKeyBindingState, SettingsFontSelectionState
    ///   InfoBaseState → CharacterState, AttributeState
    ///   LoadSaveBaseMenuState → LoadMenuState, SaveMenuState
    /// </summary>
    public enum GameMode
    {
        Unknown,
        MainMenu,
        Overland,         // Top-down exploration
        Combat,           // Rank-based tactical combat
        CombatPlacement,  // Pre-combat deployment
        CombatPlanning,   // Choosing actions during combat
        CombatResolve,    // Actions resolving
        Scene,            // Dialogue/CYOA
        Inventory,        // Item management
        CharacterSheet,   // Character stats/info
        Trade,            // Buying/selling
        Journal,          // Quest log / journal
        Settings,         // Game settings
        Camping,          // Rest screen
        Crafting,         // Crafting interface
        LevelUp,          // Level up / feat purchase
        LoadSave,         // Load/save menus
        CharacterCreation,// Character creation
        Cutscene,         // Cutscene playback
        GameOver,         // Death/game over
        Credits           // Credits screen
    }

    public static class GameStateTracker
    {
        private static GameMode _currentMode = GameMode.Unknown;
        private static GameMode _previousMode = GameMode.Unknown;
        private static string _currentStateName = "";

        /// <summary>Current game mode.</summary>
        public static GameMode CurrentMode => _currentMode;

        /// <summary>Raw state class name (for logging/debugging).</summary>
        public static string CurrentStateName => _currentStateName;

        /// <summary>
        /// Called once per actual state change, from the Pump's drain (the setState
        /// clock diffed at end of frame). Receives the live state object so reads
        /// happen against game truth at a settled moment.
        /// </summary>
        public static void OnStateChanged(string stateName, object stateObject)
        {
            if (string.IsNullOrEmpty(stateName) || stateName == _currentStateName)
                return;

            _previousMode = _currentMode;
            _currentStateName = stateName;
            _currentMode = ClassifyState(stateName);

            if (_currentMode != _previousMode)
            {
                Plugin.Logger?.LogInfo($"[GameState] {_previousMode} -> {_currentMode} ({stateName})");

                // Announce mode transitions (Framework P9: explicit termination/entry)
                string announcement = GetModeTransitionAnnouncement(_previousMode, _currentMode);
                if (!string.IsNullOrEmpty(announcement))
                {
                    Scaffold.SpeechService.Say(announcement, "State");
                }

                // Announce numeric button options with their key shortcuts
                AnnounceNumericButtons(stateObject);
            }
        }

        /// <summary>Numeric labels are the keyboard truth (owner ruling
        /// 2026-08-17): drop the controller glyph prefix the game paints into
        /// the row ("A) Select" → "Select") — the number IS the key. Config
        /// docket: a controller-mode setting later restores glyph truth. The
        /// game's "..." placeholder labels resolve to the action the slot
        /// actually performs, marked unavailable (the game renders "..."
        /// exactly when the slot is inert — FeatBuyState.cs:158-165).</summary>
        internal static string TranscodeQuickLabel(string cleaned, int index, object stateObject)
        {
            if (string.IsNullOrWhiteSpace(cleaned)) return cleaned;
            var m = System.Text.RegularExpressions.Regex.Match(cleaned, "^[ABXY]\\)\\s*");
            if (m.Success) cleaned = cleaned.Substring(m.Length);
            string payload = cleaned.Trim();
            if (payload == "..." || payload == "…" || payload == "." || payload.Length == 0)
            {
                // Slot names from the same setGUIData's sibling branch; a
                // renamed state degrades to the generic word, never garbage.
                string stateName = stateObject?.GetType().Name ?? "";
                if (stateName == "FeatBuyState")
                    return index == 0 ? "Save and Exit, unavailable"
                         : index == 2 ? "Reset, unavailable" : "unavailable";
                if (stateName == "CharacterCreationFeatsState")
                    return index == 0 ? "Continue, unavailable" : "unavailable";
                return "unavailable";
            }
            return cleaned;
        }

        /// <summary>
        /// Read numeric buttons from the active state's GUIControl and announce
        /// them with their number key shortcuts (e.g., "1: Select, 2: Abort").
        /// </summary>
        private static void AnnounceNumericButtons(object stateObject)
        {
            try
            {
                // Handles from the WP8 Seams registry — any missing row was
                // logged and counted at boot.
                if (Seams.StateBase_guiControl == null || Seams.GUIControl_numericButtons == null
                    || Seams.UIButtonControlBase_getButtonsList == null || Seams.UITextBlock_content == null)
                    return;

                if (stateObject == null) return;

                object guiControl = Seams.StateBase_guiControl.GetValue(stateObject);
                if (guiControl == null) return;

                object numericButtons = Seams.GUIControl_numericButtons.GetValue(guiControl);
                if (numericButtons == null) return;

                var buttons = Seams.UIButtonControlBase_getButtonsList.Invoke(numericButtons, null) as IList;
                if (buttons == null || buttons.Count == 0) return;

                var parts = new System.Collections.Generic.List<string>();
                for (int i = 0; i < buttons.Count; i++)
                {
                    object button = buttons[i];
                    if (button == null) continue;
                    string raw = Seams.UITextBlock_content.GetValue(button) as string;
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string cleaned = TextCleaner.CleanText(raw);
                    if (string.IsNullOrWhiteSpace(cleaned)) continue;
                    parts.Add($"{i + 1}: {TranscodeQuickLabel(cleaned, i, stateObject)}");
                }

                if (parts.Count > 0)
                {
                    string buttonList = string.Join(", ", parts);
                    Scaffold.SpeechService.SayQueued(buttonList, "NumericButtons");
                    Plugin.Logger?.LogInfo($"[State:buttons] {buttonList}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[State:buttons] {ex.Message}");
            }
        }

        /// <summary>
        /// Map state class name to GameMode.
        /// </summary>
        private static GameMode ClassifyState(string stateName)
        {
            // Combat states
            if (stateName.Contains("CombatPlacement")) return GameMode.CombatPlacement;
            if (stateName.Contains("CombatPlanning")) return GameMode.CombatPlanning;
            if (stateName.Contains("CombatResolve")) return GameMode.CombatResolve;
            if (stateName.Contains("CombatOver")) return GameMode.Combat;
            if (stateName.Contains("CombatStart")) return GameMode.Combat;
            if (stateName.Contains("CombatLog")) return GameMode.Combat;
            if (stateName.Contains("Combat")) return GameMode.Combat;

            // Scene/dialogue
            if (stateName.Contains("Scene")) return GameMode.Scene;

            // Overland exploration
            if (stateName.Contains("Overland")) return GameMode.Overland;

            // Inventory and trade
            if (stateName.Contains("Trade")) return GameMode.Trade;
            if (stateName.Contains("Inventory")) return GameMode.Inventory;

            // Character info
            if (stateName.Contains("CharacterState") || stateName.Contains("AttributeState"))
                return GameMode.CharacterSheet;
            if (stateName.Contains("Abilities") || stateName.Contains("Spells"))
                return GameMode.CharacterSheet;

            // Journal
            if (stateName.Contains("Journal")) return GameMode.Journal;

            // Settings
            if (stateName.Contains("Settings")) return GameMode.Settings;

            // Camping
            if (stateName.Contains("CampActivity")) return GameMode.Camping;

            // Crafting
            if (stateName.Contains("Crafting")) return GameMode.Crafting;

            // Level up / feats
            if (stateName.Contains("FeatBuy") || stateName.Contains("LevelUp"))
                return GameMode.LevelUp;

            // Character creation (includes DifficultySelector which is the first CC step)
            if (stateName.Contains("CharacterCreation") || stateName.Contains("CharacterBuilder")
                || stateName.Contains("DifficultySelector"))
                return GameMode.CharacterCreation;

            // Load/save
            if (stateName.Contains("LoadMenu") || stateName.Contains("SaveMenu") ||
                stateName.Contains("LoadModule"))
                return GameMode.LoadSave;

            // Game over
            if (stateName.Contains("GameOver") || stateName.Contains("Defeat"))
                return GameMode.GameOver;
            if (stateName.Contains("GameWin")) return GameMode.GameOver;

            // Credits
            if (stateName.Contains("Credits")) return GameMode.Credits;

            // Menus
            if (stateName.Contains("Menu") || stateName.Contains("IntroMenu") ||
                stateName.Contains("PreIntro") || stateName.Contains("GameStartSplash") ||
                stateName.Contains("DemoOverSplash"))
                return GameMode.MainMenu;

            // Random encounters
            if (stateName.Contains("RandomEncounter")) return GameMode.Scene;

            return GameMode.Unknown;
        }

        /// <summary>
        /// Generate a spoken announcement for mode transitions.
        /// Framework P9: explicit state termination prevents ambiguity.
        /// </summary>
        private static string GetModeTransitionAnnouncement(GameMode from, GameMode to)
        {
            switch (to)
            {
                case GameMode.Combat:
                case GameMode.CombatPlacement:
                    return "Combat";
                case GameMode.CombatPlanning:
                    return null; // Sub-state, don't announce separately
                case GameMode.CombatResolve:
                    return null; // Sub-state
                case GameMode.Scene:
                    return "Dialogue";
                case GameMode.Overland:
                    if (from == GameMode.Combat || from == GameMode.CombatPlacement ||
                        from == GameMode.CombatPlanning || from == GameMode.CombatResolve)
                        return "Combat ended";
                    if (from == GameMode.Scene)
                        return "Dialogue ended";
                    return null;
                case GameMode.Inventory:
                    return "Inventory";
                case GameMode.Trade:
                    return "Trade";
                case GameMode.CharacterSheet:
                    return "Character sheet";
                case GameMode.Journal:
                    return "Journal";
                case GameMode.Settings:
                    return "Settings";
                case GameMode.Camping:
                    return "Camping";
                case GameMode.Crafting:
                    return "Crafting";
                case GameMode.LevelUp:
                    return "Level up";
                case GameMode.LoadSave:
                    return null; // Menu-level, don't announce
                case GameMode.GameOver:
                    return "Game over";
                case GameMode.CharacterCreation:
                    return "Character creation";
                default:
                    return null;
            }
        }

        // WP8: PreviousMode / IsActiveGameplay / IsInCombat deleted — no
        // consumers, and the mode mirrors were exactly the proxy-determiner
        // shape the discipline bans (anything gated on game state asks the
        // game's own current state at time of use).
    }
}

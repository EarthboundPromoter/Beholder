using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
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

        // Reflection for reading numeric buttons from active state's GUIControl
        private static FieldInfo _stateGuiControlField;
        private static FieldInfo _numericButtonsField;
        private static MethodInfo _getButtonsListMethod;
        private static FieldInfo _contentField;
        private static bool _buttonReflectionInitialized;

        /// <summary>Current game mode.</summary>
        public static GameMode CurrentMode => _currentMode;

        /// <summary>Previous game mode (for transition detection).</summary>
        public static GameMode PreviousMode => _previousMode;

        /// <summary>Raw state class name (for logging/debugging).</summary>
        public static string CurrentStateName => _currentStateName;

        /// <summary>
        /// Called when we detect a state change (from Harmony patches or polling).
        /// Maps the state class name to our GameMode enum.
        /// </summary>
        public static void OnStateChanged(string stateName)
        {
            if (string.IsNullOrEmpty(stateName) || stateName == _currentStateName)
                return;

            _previousMode = _currentMode;
            _currentStateName = stateName;
            _currentMode = ClassifyState(stateName);

            if (_currentMode != _previousMode)
            {
                Plugin.Logger?.LogInfo($"[GameState] {_previousMode} -> {_currentMode} ({stateName})");

                // Clear caches so the new screen gets fresh speech
                ContentSpeechPatch.ClearAll();
                SliderHoverPatch.LastFocusedSliderButton = null;

                // Announce mode transitions (Framework P9: explicit termination/entry)
                string announcement = GetModeTransitionAnnouncement(_previousMode, _currentMode);
                if (!string.IsNullOrEmpty(announcement))
                {
                    Plugin.Speech?.Speak(announcement, "State");
                }

                // Announce numeric button options with their key shortcuts
                AnnounceNumericButtons();
            }
        }

        /// <summary>
        /// Read numeric buttons from the active state's GUIControl and announce
        /// them with their number key shortcuts (e.g., "1: Select, 2: Abort").
        /// </summary>
        private static void AnnounceNumericButtons()
        {
            try
            {
                if (!_buttonReflectionInitialized)
                {
                    _buttonReflectionInitialized = true;
                    var stateBaseType = AccessTools.TypeByName("StateBase");
                    var buttonBaseType = AccessTools.TypeByName("UIButtonControlBase");
                    if (stateBaseType != null)
                        _stateGuiControlField = AccessTools.Field(stateBaseType, "guiControl");
                    if (buttonBaseType != null)
                        _getButtonsListMethod = AccessTools.Method(buttonBaseType, "getButtonsList");
                    _numericButtonsField = AccessTools.Field(typeof(GUIControl), "numericButtons");
                    _contentField = AccessTools.Field(typeof(UITextBlock), "content");
                }

                if (_stateGuiControlField == null || _numericButtonsField == null
                    || _getButtonsListMethod == null || _contentField == null)
                    return;

                // Read active state from StateTransitionPatch's cached state object
                object stateControl = StateTransitionPatch.GetActiveStateObject();
                if (stateControl == null) return;

                object guiControl = _stateGuiControlField.GetValue(stateControl);
                if (guiControl == null) return;

                object numericButtons = _numericButtonsField.GetValue(guiControl);
                if (numericButtons == null) return;

                var buttons = _getButtonsListMethod.Invoke(numericButtons, null) as IList;
                if (buttons == null || buttons.Count == 0) return;

                var parts = new System.Collections.Generic.List<string>();
                for (int i = 0; i < buttons.Count; i++)
                {
                    object button = buttons[i];
                    if (button == null) continue;
                    string raw = _contentField.GetValue(button) as string;
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string cleaned = TextInterceptPatch.CleanText(raw);
                    if (string.IsNullOrWhiteSpace(cleaned)) continue;
                    parts.Add($"{i + 1}: {cleaned}");
                }

                if (parts.Count > 0)
                {
                    string buttonList = string.Join(", ", parts);
                    Plugin.Speech?.SpeakQueued(buttonList, "NumericButtons");
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

        /// <summary>
        /// Whether we're in an active gameplay mode (vs menu/settings/cutscene).
        /// Used for P11 context-dependent relevance.
        /// </summary>
        public static bool IsActiveGameplay =>
            _currentMode == GameMode.Overland ||
            _currentMode == GameMode.Combat ||
            _currentMode == GameMode.CombatPlacement ||
            _currentMode == GameMode.CombatPlanning ||
            _currentMode == GameMode.CombatResolve ||
            _currentMode == GameMode.Scene;

        /// <summary>Whether we're in any combat sub-state.</summary>
        public static bool IsInCombat =>
            _currentMode == GameMode.Combat ||
            _currentMode == GameMode.CombatPlacement ||
            _currentMode == GameMode.CombatPlanning ||
            _currentMode == GameMode.CombatResolve;
    }
}

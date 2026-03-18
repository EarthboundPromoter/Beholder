using HarmonyLib;
using System;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Detects game state transitions by patching StateControl.
    ///
    /// StateControl manages the game's state machine. Key methods:
    ///   - activateState: transitions to a new state
    ///   - jumpToState: direct state jump (may bypass normal flow)
    ///
    /// Each state is a StateBase subclass. We detect the type name
    /// and route it through GameStateTracker.
    ///
    /// Approach: We patch the StateBase base class methods that are called
    /// when states activate. Since we can't know the exact method signature
    /// without full decompilation, we start with a general approach —
    /// polling the active GUI control type each frame in Plugin.Update().
    ///
    /// We also hook GUIControl setter on MainControl if possible.
    /// </summary>
    public static class StateTransitionPatch
    {
        // Cached reflection for reading current state
        private static FieldInfo _guiControlField;
        private static PropertyInfo _guiControlProp;
        private static FieldInfo _gameControlField;
        private static Type _mainControlType;
        private static object _mainControlInstance;
        private static bool _initialized;
        private static string _lastPolledState;
        private static int _findAttempts;

        /// <summary>
        /// Initialize reflection handles. Call once from Plugin.Awake().
        /// </summary>
        public static void Initialize()
        {
            try
            {
                _mainControlType = AccessTools.TypeByName("MainControl");
                if (_mainControlType == null)
                {
                    Plugin.Logger?.LogError("[StateTransition] MainControl type not found");
                    return;
                }

                // Log MainControl's type hierarchy for diagnostics
                var baseType = _mainControlType.BaseType;
                Plugin.Logger?.LogInfo($"[StateTransition] MainControl base: {baseType?.Name ?? "none"}");

                // Try guiControl first, then fall back to gameControl (StateControl)
                _guiControlField = AccessTools.Field(_mainControlType, "guiControl");
                if (_guiControlField != null)
                {
                    Plugin.Logger?.LogInfo($"[StateTransition] Found guiControl field on {_guiControlField.DeclaringType?.Name}, type={_guiControlField.FieldType.Name}");
                }
                else
                {
                    _guiControlProp = AccessTools.Property(_mainControlType, "guiControl");
                    if (_guiControlProp != null)
                    {
                        Plugin.Logger?.LogInfo($"[StateTransition] Found guiControl property on {_guiControlProp.DeclaringType?.Name}");
                    }
                }

                // Also get gameControl (StateControl) — this is the state machine
                _gameControlField = AccessTools.Field(_mainControlType, "gameControl");
                if (_gameControlField != null)
                {
                    Plugin.Logger?.LogInfo($"[StateTransition] Found gameControl field on {_gameControlField.DeclaringType?.Name}, type={_gameControlField.FieldType.Name}");
                }
                else
                {
                    Plugin.Logger?.LogWarning("[StateTransition] gameControl not found either. Scanning fields...");
                    var allFields = AccessTools.GetDeclaredFields(_mainControlType);
                    foreach (var f in allFields)
                    {
                        string fname = f.Name.ToLower();
                        if (fname.Contains("control") || fname.Contains("gui") || fname.Contains("state"))
                        {
                            Plugin.Logger?.LogInfo($"[StateTransition]   field: {f.Name} ({f.FieldType.Name}) on {f.DeclaringType?.Name}");
                        }
                    }
                }

                _initialized = true;
                Plugin.Logger?.LogInfo("[StateTransition] State detection initialized");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[StateTransition] Init failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Poll the current state. Called from Plugin.Update().
        /// Detects state changes by checking the type of the active GUI control
        /// or state object.
        /// </summary>
        public static void PollState()
        {
            if (!_initialized) return;

            try
            {
                // Find MainControl instance — limit attempts to avoid per-frame FindObjectOfType
                if (_mainControlInstance == null)
                {
                    _findAttempts++;
                    if (_findAttempts % 60 != 0) return; // Only try once per second
                    _mainControlInstance = UnityEngine.Object.FindObjectOfType(
                        _mainControlType) as UnityEngine.Object;
                    if (_mainControlInstance == null) return;
                    Plugin.Logger?.LogInfo("[StateTransition] Found MainControl instance");
                }

                // Strategy 1: Read guiControl type name (if available)
                object guiControl = null;
                if (_guiControlField != null)
                {
                    guiControl = _guiControlField.GetValue(_mainControlInstance);
                }
                else if (_guiControlProp != null)
                {
                    guiControl = _guiControlProp.GetValue(_mainControlInstance);
                }

                if (guiControl != null)
                {
                    string typeName = guiControl.GetType().Name;
                    if (typeName != _lastPolledState)
                    {
                        _lastPolledState = typeName;
                        string stateName = MapGUIControlToState(typeName);
                        GameStateTracker.OnStateChanged(stateName);
                    }
                    return; // guiControl is authoritative if available
                }

                // Strategy 2: Read gameControl (StateControl) and get active state
                if (_gameControlField != null)
                {
                    object stateControl = _gameControlField.GetValue(_mainControlInstance);
                    if (stateControl != null)
                    {
                        string stateName = ReadActiveState(stateControl);
                        if (stateName != null && stateName != _lastPolledState)
                        {
                            _lastPolledState = stateName;
                            GameStateTracker.OnStateChanged(stateName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Don't spam logs — this runs every frame
                if (_initialized)
                {
                    Plugin.Logger?.LogDebug($"[StateTransition] Poll error: {ex.Message}");
                    _initialized = false; // Stop polling on error
                }
            }
        }

        // Cached reflection for StateControl's active state field
        private static FieldInfo _activeStateField;
        private static bool _activeStateSearched;

        /// <summary>
        /// Read the active state name from a StateControl object via reflection.
        /// StateControl likely has a field like "activeState" or "currentState" of type StateBase.
        /// </summary>
        private static string ReadActiveState(object stateControl)
        {
            if (!_activeStateSearched)
            {
                _activeStateSearched = true;
                var scType = stateControl.GetType();

                // Try common field names for the active state
                string[] candidates = { "activeState", "currentState", "state", "_activeState", "_currentState" };
                foreach (var name in candidates)
                {
                    _activeStateField = AccessTools.Field(scType, name);
                    if (_activeStateField != null)
                    {
                        Plugin.Logger?.LogInfo($"[StateTransition] Found active state field: {name} ({_activeStateField.FieldType.Name}) on {scType.Name}");
                        break;
                    }
                }

                // If none found, scan for StateBase-typed fields
                if (_activeStateField == null)
                {
                    Plugin.Logger?.LogWarning("[StateTransition] No known active state field. Scanning StateControl fields...");
                    foreach (var f in AccessTools.GetDeclaredFields(scType))
                    {
                        Plugin.Logger?.LogInfo($"[StateTransition]   SC field: {f.Name} ({f.FieldType.Name})");
                        if (f.FieldType.Name.Contains("State") && !f.FieldType.Name.Contains("Control"))
                        {
                            _activeStateField = f;
                            Plugin.Logger?.LogInfo($"[StateTransition] Using state field: {f.Name}");
                            break;
                        }
                    }
                }
            }

            if (_activeStateField == null) return null;

            object activeState = _activeStateField.GetValue(stateControl);
            return activeState?.GetType().Name;
        }

        /// <summary>
        /// Returns the active StateBase object. Used by GameStateTracker to read
        /// the state's guiControl for numeric button announcements.
        /// </summary>
        public static object GetActiveStateObject()
        {
            try
            {
                if (_mainControlInstance == null || _gameControlField == null || _activeStateField == null)
                    return null;
                object stateControl = _gameControlField.GetValue(_mainControlInstance);
                if (stateControl == null) return null;
                return _activeStateField.GetValue(stateControl);
            }
            catch { return null; }
        }

        /// <summary>
        /// Map a GUIControl class name to a state-like name for GameStateTracker.
        /// </summary>
        private static string MapGUIControlToState(string guiControlName)
        {
            // The GUIControl* classes map directly to game modes
            switch (guiControlName)
            {
                case "GUIControlScene": return "SceneState";
                case "GUIControlLog": return "OverlandState";
                case "GUIControlOverland": return "OverlandState";
                case "GUIControlCombat": return "CombatPlanningState";
                case "GUIControlCombatEffectTargeting": return "CombatPlanningState";
                case "GUIControlOverlandEffectTargeting": return "OverlandState";
                case "GUIControlInventory": return "InventoryState";
                case "GUIControlInventoryBase": return "InventoryState";
                case "GUIControlCharacterSheet": return "CharacterState";
                case "GUIControlMenu": return "MenuState";
                case "GUIControlSettings": return "SettingsState";
                case "GUIControlCamping": return "CampActivityState";
                case "GUIControlCrafting": return "CraftingState";
                case "GUIControlContainer": return "InventoryState";
                case "GUIControlFeatBuy": return "FeatBuyState";
                case "GUIControlCredits": return "CreditsState";
                case "GUIControlAbilitySheet": return "AbilitiesState";
                case "GUIControlSpellBookSheet": return "SpellsState";
                case "GUIControlExtraRowList": return "JournalState";
                case "GUIControlPartyManagement": return "InventoryState";
                case "GUIControlApperanceEditorSheet": return "CharacterCreationState";
                case "GUIControlSheet": return "CharacterState";
                default:
                    return guiControlName; // Pass through unknown names
            }
        }
    }
}

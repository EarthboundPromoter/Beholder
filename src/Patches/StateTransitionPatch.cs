using HarmonyLib;
using System;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Detects game state transitions. Despite the name this is currently a
    /// per-frame poller, not a Harmony patch: it reads the static
    /// MainControl.gameControl (StateControl) field and its currentState, and
    /// reports type-name changes to GameStateTracker.
    ///
    /// INTERIM: build-plan WP3 replaces this with a note-only postfix on
    /// StateControl.setState(SkaldStates) (decompiled MainControl.cs:212) plus
    /// settled truth-reads at the end-of-frame drain, and this poller is deleted.
    /// </summary>
    public static class StateTransitionPatch
    {
        // Cached reflection for reading current state
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

                // gameControl (StateControl) is the state machine. (MainControl has
                // no guiControl member — guiControl lives on StateBase.)
                _gameControlField = AccessTools.Field(_mainControlType, "gameControl");
                if (_gameControlField == null)
                {
                    Plugin.Logger?.LogError("[StateTransition] gameControl field not found on MainControl");
                    return;
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
        /// Transient failures (loading frames, scene churn) are logged and skipped —
        /// polling continues on the next frame.
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
            catch (Exception ex)
            {
                // Transient — a loading frame or destroyed object. Keep polling;
                // going permanently silent here once disabled the entire mod.
                Plugin.Logger?.LogDebug($"[StateTransition] Poll error (continuing): {ex.Message}");
            }
        }

        // Cached reflection for StateControl's active state field
        private static FieldInfo _activeStateField;
        private static bool _activeStateSearched;

        /// <summary>
        /// Read the active state name from the StateControl object via reflection.
        /// The field is currentState (StateBase) — candidates kept for resilience.
        /// </summary>
        private static string ReadActiveState(object stateControl)
        {
            if (!_activeStateSearched)
            {
                _activeStateSearched = true;
                var scType = stateControl.GetType();

                string[] candidates = { "currentState", "activeState", "state" };
                foreach (var name in candidates)
                {
                    _activeStateField = AccessTools.Field(scType, name);
                    if (_activeStateField != null)
                    {
                        Plugin.Logger?.LogInfo($"[StateTransition] Found active state field: {name} ({_activeStateField.FieldType.Name}) on {scType.Name}");
                        break;
                    }
                }

                if (_activeStateField == null)
                    Plugin.Logger?.LogError("[StateTransition] No active state field found on StateControl");
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
    }
}

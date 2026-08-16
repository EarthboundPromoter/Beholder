using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SkaldAccessibility
{
    /// <summary>
    /// The timing spine (build-plan WP3, lineage: Vantage Pump.cs / OAG approved
    /// architecture). Harmony hooks NOTE what fired and nothing else; all reads and
    /// composition happen here, once per frame, at LateUpdate — after the game's own
    /// update has settled. Frame-guarded, exception-proof: the pump never dies.
    ///
    /// Streams (added per work package):
    ///   WP3: state clock — StateChangePatch notes the StateControl instance;
    ///        the drain reads currentState (game truth, no mod mirror) and diffs
    ///        its type name once at the clock. Replaces the per-frame PollState
    ///        poller and its FindObjectOfType hunt.
    ///   WP4 (planned): selection join. WP5 (planned): content sources.
    /// </summary>
    public static class Pump
    {
        // ---- State stream (noted by StateChangePatch; latest wins) ----
        private static object _stateControl;
        private static FieldInfo _currentStateField;
        private static string _lastStateName;

        private static int _lastFrame = -1;

        /// <summary>Note-only: called from the setState postfix. The game calls
        /// setState every frame from StateControl.update(), so this doubles as the
        /// game-owned clock — every currentState write path (including the direct
        /// assignments inside setState and the boot-time constructor) is visible to
        /// the drain's diff at worst one frame later.</summary>
        public static void NoteStateControl(object stateControl)
        {
            _stateControl = stateControl;
        }

        /// <summary>Live game-truth read of the active StateBase — never a cached
        /// mod-side copy of the state itself.</summary>
        public static object CurrentStateObject()
        {
            var sc = _stateControl;
            if (sc == null) return null;
            if (_currentStateField == null)
            {
                _currentStateField = AccessTools.Field(sc.GetType(), "currentState");
                if (_currentStateField == null) return null;
            }
            return _currentStateField.GetValue(sc);
        }

        /// <summary>Called from Plugin.LateUpdate. Drain order encodes precedence;
        /// SpeechService.Tick runs last so anything drained this frame can still
        /// enter the queue ahead of the pump.</summary>
        public static void Drain()
        {
            if (Time.frameCount == _lastFrame) return;
            _lastFrame = Time.frameCount;

            try { DrainState(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:state] {ex.Message}"); }

            try { Scaffold.SpeechService.Tick(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[Pump:speech] {ex.Message}"); }
        }

        private static void DrainState()
        {
            object state = CurrentStateObject();
            if (state == null) return;
            string name = state.GetType().Name;
            if (name == _lastStateName) return;
            _lastStateName = name;
            GameStateTracker.OnStateChanged(name, state);
        }
    }
}

using UnityEngine;
using SkaldAccessibility.Scaffold;

namespace SkaldAccessibility
{
    /// <summary>
    /// Handles custom hotkeys for the accessibility mod.
    /// Called from Plugin.Update() every frame.
    ///
    /// Uses Unity legacy Input (same as game). Current keys predate the
    /// 2026-08-16 forced-controller ruling; the full layout (and the known
    /// F2-vs-native-Feedback collision) is settled at the keymap session
    /// before build-plan WP7.
    ///
    /// Hotkeys:
    ///   /              - Stop speech (flushes the queue too — explicit act)
    ///   F1             - Repeat last spoken text
    ///   F2             - Announce current game mode/state
    ///   [              - Speech history: previous
    ///   ]              - Speech history: next
    /// </summary>
    public static class InputHandler
    {
        public static void ProcessInput()
        {
            // Stop speech: /
            if (Input.GetKeyDown(KeyCode.Slash))
            {
                SpeechService.Stop();
            }

            // Repeat last speech: F1
            if (Input.GetKeyDown(KeyCode.F1))
            {
                SpeechService.RepeatLast();
            }

            // Announce current state: F2
            if (Input.GetKeyDown(KeyCode.F2))
            {
                var mode = GameStateTracker.CurrentMode;
                string stateName = GameStateTracker.CurrentStateName;
                SpeechService.Say($"{mode}. {stateName}", "Hotkey");
            }

            // Speech history browse: [ and ] — reads the ring, never mutates it.
            if (Input.GetKeyDown(KeyCode.LeftBracket))
            {
                SpeechService.HistoryPrevious();
            }
            if (Input.GetKeyDown(KeyCode.RightBracket))
            {
                SpeechService.HistoryNext();
            }
        }
    }
}

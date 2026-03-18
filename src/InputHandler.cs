using UnityEngine;
using SkaldAccessibility.Patches;
using SkaldAccessibility.Speech;

namespace SkaldAccessibility
{
    /// <summary>
    /// Handles custom hotkeys for the accessibility mod.
    /// Called from Plugin.Update() every frame.
    ///
    /// Uses Unity legacy Input (same as game). Keys chosen to avoid
    /// game conflicts (Tab=console, I=inventory, C=char, J=journal,
    /// F5/F9=quicksave/load, 1-9=options, arrows=movement).
    ///
    /// Hotkeys:
    ///   /              - Cancel current speech
    ///   F1             - Repeat last spoken text
    ///   F2             - Announce current game mode/state
    ///   [              - Speech history: previous
    ///   ]              - Speech history: next
    ///   F12            - Re-check screen reader availability
    /// </summary>
    public static class InputHandler
    {
        public static void ProcessInput()
        {
            // Cancel speech: /
            if (Input.GetKeyDown(KeyCode.Slash))
            {
                Plugin.Speech?.Cancel();
            }

            // Repeat last speech: F1
            if (Input.GetKeyDown(KeyCode.F1))
            {
                string last = Plugin.Speech?.LastSpoken;
                if (!string.IsNullOrEmpty(last))
                {
                    Plugin.Speech?.Speak(last, "Hotkey");
                }
            }

            // Announce current state: F2
            if (Input.GetKeyDown(KeyCode.F2))
            {
                var mode = GameStateTracker.CurrentMode;
                string stateName = GameStateTracker.CurrentStateName;
                Plugin.Speech?.Speak($"{mode}. {stateName}", "Hotkey");
            }

            // Speech history navigation: [ and ]
            if (Input.GetKeyDown(KeyCode.LeftBracket))
            {
                string prev = SpeechHistory.Instance.Previous();
                if (prev != null) Plugin.Speech?.Speak(prev, "Hotkey");
            }
            if (Input.GetKeyDown(KeyCode.RightBracket))
            {
                string next = SpeechHistory.Instance.Next();
                if (next != null) Plugin.Speech?.Speak(next, "Hotkey");
            }

            // Re-check screen reader: F12
            if (Input.GetKeyDown(KeyCode.F12))
            {
                Plugin.Speech?.Recheck();
                if (Plugin.Speech?.IsAvailable == true)
                {
                    Plugin.Speech.Speak("Screen reader reconnected.", "Hotkey");
                }
                else
                {
                    Plugin.Logger?.LogWarning("Screen reader still not available.");
                }
            }
        }
    }
}

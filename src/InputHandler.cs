using UnityEngine;
using SkaldAccessibility.Scaffold;

namespace SkaldAccessibility
{
    /// <summary>
    /// Handles custom hotkeys for the accessibility mod.
    /// Called from Plugin.Update() every frame.
    ///
    /// Uses Unity legacy Input (same as game). Layout per the keymap session
    /// (2026-08-16): the F2 mode-announce key is deleted (owner ruling — native
    /// F2 Feedback keeps the key untouched). All review keys suspend while the
    /// game's own text entry is capturing, same gate as the controller feed.
    ///
    /// Hotkeys:
    ///   /              - Stop speech (flushes the queue too — explicit act)
    ///   F1             - Repeat last spoken text
    ///   [              - Speech history: previous
    ///   ]              - Speech history: next
    /// </summary>
    public static class InputHandler
    {
        public static void ProcessInput()
        {
            if (Patches.ControllerFeedPatch.TextEntryActive()) return;

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

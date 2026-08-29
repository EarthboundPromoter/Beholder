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
    ///   F1             - Contextual key table (owner design 2026-08-29; the
    ///                    repeat key it displaced is retired outright)
    ///   [              - Speech history: previous
    ///   ]              - Speech history: next
    ///   R              - Review toggle (WP10); Home/End + PgUp/PgDn = the
    ///                    stateless review cluster, live in every state
    /// </summary>
    public static class InputHandler
    {
        public static void ProcessInput()
        {
            if (Patches.ControllerFeedPatch.TextEntryActive()) return;

            // Input edges (L1): one Debug line per watched key-down with the
            // state and modality owners — before any handler consumes it.
            Scaffold.Log.InputEdges();

            // Stop speech: / — processed before everything; the silencer is
            // sacred and works inside the review state too.
            if (Input.GetKeyDown(KeyCode.Slash))
            {
                SpeechService.Stop();
            }

            // The key table (F1): a static two-column browse over the current
            // surface's keys. While open it consumes everything except the
            // speech-control keys. Runs before every driving layer.
            if (KeyTable.ProcessInput()) return;

            // The review layer (WP10): toggle, cluster, and in-state input
            // classes. Consumed presses stop here.
            if (ReviewLayer.ProcessInput()) return;

            // Selector-grid driver (WP9): while a grid is open, the option
            // accessors (WASD under the feed) walk it through the game's own
            // funnel calls. No-op when no grid is live.
            Patches.GridNavigationPatch.Tick();

            // Table cursor (table-ui gate A): on registered UI screens the
            // arrows walk rows through the game's own funnel calls and W/S
            // walks sections (R15). Suspends itself under popups/grids/text
            // entry. Consumed presses stop here.
            if (TableCursor.ProcessInput()) return;

            // Overland cursor (WP11): arrows nudge, scan keys jump, K opens
            // the catalog list. Consumed presses stop here. The latch tick
            // runs regardless (re-asserts the virtual mouse each frame).
            bool cursorConsumed = OverlandCursor.ProcessInput();
            OverlandCursor.Tick();
            if (cursorConsumed) return;

            // Combat cursor (CP3): same contract, combat states only — the
            // two gates are disjoint so exactly one cursor is ever live.
            bool combatConsumed = CombatCursor.ProcessInput();
            CombatCursor.Tick();
            if (combatConsumed) return;

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

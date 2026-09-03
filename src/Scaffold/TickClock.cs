namespace SkaldAccessibility.Scaffold
{
    /// <summary>
    /// The game's clock, for mod timing (frame-rate audit 2026-09-03).
    ///
    /// SKALD advances ALL of its logic — state updates, UI mutation, text
    /// re-renders, selection cascades, movement — in FixedUpdate ticks of
    /// exactly 1/60 s, independent of the render rate. Unity renders 60
    /// frames per tick-second on a 60 Hz display, 240 on a 240 Hz one, and
    /// two ticks can share one frame below 60 fps. Every mod window that
    /// waits for a GAME process must therefore count ticks, not frames: a
    /// "one frame" deferral is a whole tick at 60 fps and nothing at all at
    /// 240; a 45-frame window is 0.75 s at 60 fps and 0.19 s at 240. Frames
    /// stay the right unit for exactly one thing — the edge of a key press,
    /// which is a display-time event (ControllerFeedPatch's latch turns
    /// those into tick events).
    ///
    /// Now counts completed ticks (one per SkaldIO.clear, the end of every
    /// tick; ControllerFeedPatch.Postfix_Clear). A tick BODY runs before its
    /// own increment: a stamp taken in the Update phase after tick T reads
    /// T, and the next tick's body still reads T — "Moment &gt; stamp" is
    /// therefore true from the first LateUpdate after a full tick has run.
    /// Live is true once that postfix has been observed; until then Moment
    /// falls back to the render frame, which is the pre-audit behaviour.
    /// </summary>
    internal static class TickClock
    {
        /// <summary>Completed game ticks this session.</summary>
        public static long Now => Patches.ControllerFeedPatch.TickCount;

        /// <summary>True once the tick boundary has been observed running.</summary>
        public static bool Live => Patches.ControllerFeedPatch.TickClockLive;

        /// <summary>The timing unit for windows and deferrals: ticks while
        /// the clock is live, render frames before that (the fallback keeps
        /// every window at its pre-audit size until the boundary is proven).
        /// Constants written in ticks are seconds × 60 at every frame rate.
        /// Continuous across the flip: the first live read rebases the tick
        /// count onto the frame count it replaces, so a mark taken in frames
        /// before the boundary was proven still compares as "recent" after
        /// it — the raw count would have jumped from thousands of frames to
        /// a handful of ticks and held every such mark in the future.</summary>
        public static long Moment
        {
            get
            {
                if (!Live) return UnityEngine.Time.frameCount;
                if (!_rebased)
                {
                    _rebased = true;
                    _rebase = UnityEngine.Time.frameCount - Now;
                }
                return Now + _rebase;
            }
        }

        private static bool _rebased;
        private static long _rebase;

        /// <summary>Key for once-per-moment memos that are read both inside
        /// ticks and in the render phase: changes when EITHER the tick or
        /// the frame changes. A frame-only key served a tick-1 answer to
        /// tick 2 of the same frame (30 fps) and an Update-phase answer to
        /// the next tick (its body runs before its increment).</summary>
        public static long MemoKey => (Now << 32) | (uint)UnityEngine.Time.frameCount;
    }
}

using UnityEngine;

namespace SkaldAccessibility
{
    /// <summary>
    /// The eat tail, measured in game ticks (frame-rate audit 2026-09-03).
    ///
    /// A mod layer that closes on a key press must keep the game blind to
    /// that press: SkaldIO.update latches the key-down into the game's
    /// keyPressed list in the same render frame, and the game reads it on
    /// its NEXT FixedUpdate tick — which at 60 fps is the next frame, at
    /// 240 fps up to four frames later, and at 30 fps the same frame (twice).
    /// The tails used to be "one or two render frames", so above 60 fps they
    /// expired before the tick read the key and the closing press reached
    /// the game (Escape opened the menu, Space fired the contextual attack),
    /// while at 30 fps the two-frame tail ate one tick too many.
    ///
    /// The tick clock is ControllerFeedPatch.TickCount, incremented at the
    /// end of every tick (its SkaldIO.clear postfix). Arm records the
    /// current count; Holds is true until that count advances — i.e. through
    /// exactly the one tick that reads the latched press — at any frame
    /// rate. Until the tick boundary is confirmed running (the latch's own
    /// receipt) the pre-audit frame window stands in.
    /// </summary>
    internal sealed class EatTail
    {
        private long _tick = -1;
        private int _frame = -1;

        /// <summary>Arm from the Update phase on the closing press.
        /// <paramref name="frameFallback"/> is the pre-audit window, used
        /// only while the tick clock is not yet confirmed.</summary>
        public void Arm(int frameFallback)
        {
            _tick = Patches.ControllerFeedPatch.TickCount;
            _frame = Time.frameCount + frameFallback;
        }

        // Ceiling on the tick branch (Sonnet review 2026-09-03): if the tick
        // clock ever froze after being trusted, a tail would hold forever —
        // the pinned-key class the press latch guards its own flags against
        // with StaleFrames. No tick for this many render frames cannot
        // happen while the game runs (>7200 fps), so the ceiling never
        // shortens a real tail.
        private const int CeilingFrames = 120;

        /// <summary>True while the tail must still hold.</summary>
        public bool Holds => Patches.ControllerFeedPatch.TickClockLive
            ? Patches.ControllerFeedPatch.TickCount <= _tick && Time.frameCount <= _frame + CeilingFrames
            : Time.frameCount <= _frame;
    }
}

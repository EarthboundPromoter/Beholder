using System.Diagnostics;
using UnityEngine;

namespace SkaldAccessibility.Scaffold
{
    /// <summary>
    /// The frame-budget receipt (passive-sweep cost job, 2026-09-03).
    ///
    /// The mod's per-frame work is stopwatched in two segments — the
    /// Update-phase drivers (InputHandler.ProcessInput → the cursors → the
    /// passive sweep, the press-latch arm, the gate receipts) and the
    /// LateUpdate drain (Pump.Drain → composition → the speech pump's
    /// synchronous Tolk call) — and summed per render frame. At the end of
    /// each LateUpdate the PREVIOUS frame is judged: Time.unscaledDeltaTime
    /// in frame N is the wall duration of frame N−1 (start to start, render
    /// and vsync wait included), the mod's share of N−1 is the sum recorded
    /// there, and the ticks that ran in frame N are the catch-up the game
    /// paid for it (a frame longer than a tick makes Unity run extra
    /// FixedUpdates the next frame — the ride log's "+600f/+644 ticks").
    ///
    /// The budget a frame is judged against is the DISPLAY period when vsync
    /// is on (1000 ms × vSyncCount / refresh rate: 16.7 ms at 60 Hz, 4.2 ms
    /// at 240 Hz — a frame past 1.5× of it is a dropped frame), and the
    /// game's tick (16.7 ms) otherwise (a frame past 1.5 ticks costs a
    /// catch-up tick). Refreshed once per Clock period.
    ///
    /// Receipts: a Debug `[Frame]` line for frames the mod plausibly cost —
    /// over budget with at least 1 ms of mod work in them, or any frame with
    /// more than 4 ms of mod work (the whole budget of a 240 Hz frame) —
    /// capped per period; and a rollup on the PERIODIC Info `[Clock]` stamp
    /// only (every 600 ticks ≈ 10 s; the state-transition stamps carry no
    /// rollup and do not reset it — Opus review 2026-09-03): max mod ms
    /// over the period's frame count, frames the mod exceeded 2 ms, frames
    /// over budget with the budget named, so a player's Info-level log
    /// answers "did the mod drop frames" without Debug logging.
    ///
    /// Not counted: Harmony patch bodies (the controller feed, the combat
    /// spine, panel/text hooks, the tick-keyed memos those bodies recompute)
    /// run inside the game's own calls — their cost lands in the frame time
    /// but not in the mod share, and it is a material fraction, not a
    /// rounding error. The armed line says so. Time.unscaledDeltaTime is
    /// clamped at Time.maximumDeltaTime (0.333 s), so a longer hitch
    /// under-reports its length — still over budget, so still flagged.
    /// </summary>
    internal static class FrameBudget
    {
        private static readonly Stopwatch _sw = new Stopwatch();
        private static int _segFrame = -1;      // the frame the running sum belongs to
        private static double _segMs;           // mod ms summed in that frame
        private static int _prevFrame = -1;     // the last frame EndFrame saw
        private static double _prevMs;          // its mod share
        private static long _prevTicks = -1;    // the tick count at its EndFrame

        // The budget, refreshed per Clock period
        private static float _budgetMs = -1f;
        private static string _budgetWhy = "";

        // Rollup since the last periodic Clock stamp
        private static double _maxMs;
        private static int _periodFrames, _over2Ms, _overBudget, _lines, _suppressed;
        private const int MaxLinesPerPeriod = 12;
        private static bool _armedLogged;

        /// <summary>Start a stopwatched segment (an Update or LateUpdate body).</summary>
        internal static void Begin()
        {
            _sw.Reset();
            _sw.Start();
        }

        /// <summary>End the segment and add it to this frame's sum.</summary>
        internal static void End()
        {
            _sw.Stop();
            int f = Time.frameCount;
            if (f != _segFrame) { _segFrame = f; _segMs = 0; }
            _segMs += _sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>Judge the previous frame. Call at the end of LateUpdate,
        /// after the last End() of the frame.</summary>
        internal static void EndFrame()
        {
            int f = Time.frameCount;
            long ticks = TickClock.Now;
            if (_budgetMs < 0f) RefreshBudget();
            if (!_armedLogged)
            {
                _armedLogged = true;
                Plugin.Logger?.LogInfo("[Frame] Budget receipt armed: Update drivers + LateUpdate drain stopwatched per frame "
                    + "(Harmony patch bodies run inside game calls and are not counted); budget "
                    + $"{_budgetMs:0.0}ms ({_budgetWhy}); Frame lines at Debug name frames over 1.5x budget with >=1 ms "
                    + "of mod work or any frame with >4 ms of mod work; the Clock stamp carries the rollup");
            }
            if (_prevFrame == f - 1 && _prevTicks >= 0)
            {
                float frameMs = Time.unscaledDeltaTime * 1000f;   // wall duration of frame f-1
                double modMs = _prevMs;
                long tickDelta = ticks - _prevTicks;              // ticks that ran in frame f (catch-up for f-1)
                bool overBudget = frameMs > 1.5f * _budgetMs;
                _periodFrames++;
                if (modMs > _maxMs) _maxMs = modMs;
                if (modMs > 2.0) _over2Ms++;
                if (overBudget) _overBudget++;
                if ((overBudget && modMs >= 1.0) || modMs > 4.0)
                {
                    if (_lines < MaxLinesPerPeriod)
                    {
                        _lines++;
                        Log.Debug("Frame", $"f{f - 1} ran {frameMs:0.0}ms (budget {_budgetMs:0.0}ms); mod {modMs:0.0}ms of it; "
                            + $"{tickDelta} tick(s) ran in f{f}");
                    }
                    else _suppressed++;
                }
            }
            _prevFrame = f;
            _prevMs = _segFrame == f ? _segMs : 0;
            _prevTicks = ticks;
        }

        /// <summary>The display period when vsync is on; the player's own
        /// cap when vsync is off and Application.targetFrameRate is set (a
        /// deliberate 30 fps cap makes 33 ms frames normal — Sonnet
        /// SHOULD-FIX 2026-09-03); else the tick (uncapped: a frame longer
        /// than a tick is what costs a catch-up tick).</summary>
        private static void RefreshBudget()
        {
            float tickMs = Time.fixedDeltaTime * 1000f;
            _budgetMs = tickMs;
            _budgetWhy = "tick, vsync off, uncapped";
            try
            {
                int vs = QualitySettings.vSyncCount;
                int hz = Screen.currentResolution.refreshRate;
                int target = Application.targetFrameRate;
                if (vs > 0 && hz > 0)
                {
                    _budgetMs = 1000f * vs / hz;
                    _budgetWhy = $"display {hz} Hz, vsync {vs}";
                }
                else if (target > 0)
                {
                    _budgetMs = 1000f / target;
                    _budgetWhy = $"targetFrameRate {target}, vsync off";
                }
            }
            catch { }
            if (_budgetMs <= 0f) { _budgetMs = tickMs; _budgetWhy = "tick"; }
        }

        /// <summary>The rollup for the PERIODIC Clock stamp; resets the
        /// period. Self-describing: it names its own frame count, so it
        /// reads correctly whatever else shares the line.</summary>
        internal static string Rollup()
        {
            string s = $"mod max {_maxMs:0.0}ms over {_periodFrames} frames, {_over2Ms} frames mod>2ms, "
                + $"{_overBudget} frames >1.5x{_budgetMs:0.0}ms"
                + (_suppressed > 0 ? $", {_suppressed} Frame lines suppressed" : "");
            _maxMs = 0; _periodFrames = 0; _over2Ms = 0; _overBudget = 0; _lines = 0; _suppressed = 0;
            _budgetMs = -1f;   // re-read next frame (display or vsync may have changed)
            return s;
        }
    }
}

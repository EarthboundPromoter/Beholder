using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SkaldAccessibility.Scaffold
{
    /// <summary>
    /// Diagnosability scaffold (owner directive 2026-08-19, from the Opus
    /// session-log review): the logs must answer what the player pressed,
    /// what modality owned it, whether speech was cut, which build ran,
    /// and which seam is unhealthy — without one broken seam becoming 39%
    /// of the file (the AoE flood).
    ///
    ///  - Channel-gated Debug: config Logging.DebugChannels ("all", "none",
    ///    or a comma list, e.g. "Input,Mode,POI") — a ride can be
    ///    instrumented narrowly.
    ///  - Throttled failure logging: first occurrence speaks in full, then
    ///    every 300th with a running count; per-state-transition rollups at
    ///    Info name every key that failed since the last rollup, so seam
    ///    health is one grep ("[SeamHealth]").
    ///  - Input edges: one Debug line per watched key-down with the state
    ///    and modality flags — engagement stops being inference from
    ///    speech output.
    ///  - Wall clock: an Info stamp every ~10s and on every state
    ///    transition, so frame numbers get real time.
    /// </summary>
    internal static class Log
    {
        private static ConfigEntry<string> _cfgChannels;
        private static HashSet<string> _channels;   // null = all, empty = none

        internal static void BindConfig(ConfigFile config)
        {
            _cfgChannels = config.Bind("Logging", "DebugChannels", "all",
                "Debug-level log channels: 'all', 'none', or a comma list "
                + "(Input, Mode, POI, Compose, Seam). Info-level logging is unaffected.");
            Reparse();
            _cfgChannels.SettingChanged += (s, e) => Reparse();
        }

        private static void Reparse()
        {
            string v = _cfgChannels?.Value?.Trim() ?? "all";
            if (v.Equals("all", StringComparison.OrdinalIgnoreCase)) { _channels = null; return; }
            _channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
            foreach (string part in v.Split(','))
                if (!string.IsNullOrWhiteSpace(part)) _channels.Add(part.Trim());
        }

        internal static bool ChannelOn(string channel)
            => _channels == null || _channels.Contains(channel);

        internal static void Debug(string channel, string message)
        {
            if (!ChannelOn(channel)) return;
            Plugin.Logger?.LogDebug($"[{channel}] [f{Time.frameCount}] {message}");
        }

        // ---- Throttled failures + the health rollup ----

        private static readonly Dictionary<string, int> _failCounts
            = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _failFirstFrame
            = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _failReported
            = new Dictionary<string, int>();

        /// <summary>A recurring failure site (per-frame catch bodies): logs
        /// the first occurrence in full, then every 300th with the count.
        /// One broken seam costs a handful of lines, not the file.</summary>
        internal static void Throttled(string key, string message)
        {
            int n;
            _failCounts.TryGetValue(key, out n);
            _failCounts[key] = ++n;
            if (n == 1)
            {
                _failFirstFrame[key] = Time.frameCount;
                Plugin.Logger?.LogDebug($"[{key}] [f{Time.frameCount}] {message} (first; repeats roll up)");
            }
            else if (n % 300 == 0)
            {
                Plugin.Logger?.LogDebug(
                    $"[{key}] [f{Time.frameCount}] {message} ({n} times since f{_failFirstFrame[key]})");
            }
        }

        /// <summary>Info-level seam-health rollup — called at state
        /// transitions; names every throttled key that failed since the
        /// last rollup. "All seams resolved" can no longer mask a
        /// declaring-type mismatch that throws at USE.</summary>
        internal static void HealthRollup()
        {
            foreach (var kv in _failCounts)
            {
                int reported;
                _failReported.TryGetValue(kv.Key, out reported);
                if (kv.Value > reported)
                {
                    Plugin.Logger?.LogInfo(
                        $"[SeamHealth] {kv.Key}: {kv.Value - reported} failures since last rollup "
                        + $"({kv.Value} total since f{_failFirstFrame[kv.Key]})");
                    _failReported[kv.Key] = kv.Value;
                }
            }
        }

        // ---- Input edges (L1) ----

        private static readonly KeyCode[] Watched =
        {
            KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,
            KeyCode.W, KeyCode.A, KeyCode.S, KeyCode.D,
            KeyCode.Z, KeyCode.X, KeyCode.Space, KeyCode.Escape, KeyCode.Backspace, KeyCode.Return,
            KeyCode.H, KeyCode.N, KeyCode.B, KeyCode.O, KeyCode.M, KeyCode.P, KeyCode.K,
            KeyCode.R, KeyCode.I, KeyCode.U, KeyCode.Q, KeyCode.E, KeyCode.T, KeyCode.V,
            KeyCode.F1, KeyCode.LeftBracket, KeyCode.RightBracket, KeyCode.Slash,
            KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown,
            KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5,
            KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9,
        };

        /// <summary>One Debug line per watched key-down: the state, the live
        /// modality owners, and which swallow predicate (if any) will eat
        /// the key. Edge-only — held repeats never log.</summary>
        internal static void InputEdges()
        {
            if (!ChannelOn("Input")) return;
            for (int i = 0; i < Watched.Length; i++)
            {
                var k = Watched[i];
                if (!Input.GetKeyDown(k)) continue;
                string state = "?";
                try { state = Pump.CurrentStateObject()?.GetType().Name ?? "null"; } catch { }
                string swallow = "none";
                try
                {
                    if (ReviewLayer.ShouldSwallowKey(k)) swallow = "review";
                    else if (OverlandCursor.ShouldSwallowKey(k)) swallow = "poilist";
                    else if (CombatCursor.ShouldSwallowKey(k)) swallow = "combat";
                }
                catch { }
                Plugin.Logger?.LogDebug(
                    $"[Input] [f{Time.frameCount}] {k} state={state}"
                    + $" review={(ReviewLayer.Active ? "on" : "off")}"
                    + $" list={(OverlandCursor.ListOpen ? "open" : "closed")}"
                    + $" swallow={swallow}");
            }
        }

        // ---- Wall clock (L3) ----

        // -600, not int.MinValue: the subtraction in ClockTick overflowed on
        // the sentinel, so the periodic stamp only self-started once a state
        // transition had stamped (review note 11).
        private static int _lastClockFrame = -600;

        /// <summary>Periodic wall-clock stamp (~10s at 60fps) — call every
        /// frame; also called unconditionally at state transitions.</summary>
        internal static void ClockTick()
        {
            if (Time.frameCount - _lastClockFrame < 600) return;
            ClockNow();
        }

        internal static void ClockNow()
        {
            // Ticks since the last stamp beside the frames since it: the
            // frames-per-tick ratio is the press-latch audit's key number
            // (1.0 at 60 fps; 2.4 at 144 Hz) and now reads straight off a log.
            long ticks = Patches.ControllerFeedPatch.TickCount;
            int frames = _lastClockFrame < 0 ? 0 : Time.frameCount - _lastClockFrame;
            long tickDelta = _lastClockTicks < 0 ? 0 : ticks - _lastClockTicks;
            _lastClockFrame = Time.frameCount;
            _lastClockTicks = ticks;
            Plugin.Logger?.LogInfo(
                $"[Clock] {DateTime.Now:yyyy-MM-dd'T'HH:mm:ss} f{Time.frameCount} +{frames}f/+{tickDelta} ticks");
        }

        private static long _lastClockTicks = -1;
    }
}

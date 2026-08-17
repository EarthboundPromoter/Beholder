using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SkaldAccessibility.Scaffold
{
    internal enum Priority
    {
        /// <summary>Interrupts current speech immediately (user-initiated navigation/queries).</summary>
        Immediate,
        /// <summary>Appended to the announcement queue (dialogue, notifications, tutorials).</summary>
        Queued,
    }

    /// <summary>Central speech output: Tolk bridge, announcement queue, history, text cleaning.
    /// All calls must come from the main thread.
    ///
    /// Ported from Sleeptalker Scaffold (WP2, 2026-08-16). SKALD adaptations:
    /// convenience Say/SayQueued overloads matching this codebase's (text, source)
    /// call convention; history browse cursor (this mod's [ ] keys — reads the ring
    /// raw, never re-adds, so browsing can no longer corrupt history); frame stamp
    /// in place of the CS2 Diag mode block (Diag is not ported yet). Game-specific
    /// markup (SKALD's {script}/#c= codes) stays in TextCleaner as a caller-side
    /// pre-pass; Clean() here is the game-agnostic transcode tier.</summary>
    internal static class SpeechService
    {
        private const int HistoryCapacity = 200;

        private struct Pending
        {
            public string Text;
            public string Source;
        }

        private static bool _loaded;
        private static bool _available;
        private static readonly Queue<Pending> Queue = new Queue<Pending>();
        private static readonly List<string> History = new List<string>();
        private static string _lastQueued;
        private static float _lastSpokeAt;
        private static int _historyCursor = -1; // -1 = at the live end

        private static readonly Regex TagPattern = new Regex("<[^>]{1,64}?>", RegexOptions.Compiled);
        private static readonly Regex SpacePattern = new Regex("[ \t]{2,}", RegexOptions.Compiled);
        /// <summary>Punctuation stutters — sprite-boundary seams ("action,. select")
        /// and authored double periods — collapse to the run's first mark. EXCEPT the
        /// authored ELLIPSIS (CS2 ride 2026-08-04: the rule was eating the game's own
        /// hesitation marks). A run of three or more periods is prose: normalize to
        /// exactly three and keep it. Transcode, never strip.</summary>
        private static readonly Regex StutterPattern =
            new Regex("([.,])(?:\\s*[.,])+", RegexOptions.Compiled);

        private static readonly MatchEvaluator StutterEvaluator = CollapseStutter;

        private static string CollapseStutter(Match m)
        {
            string run = m.Value;
            for (int i = 0; i < run.Length; i++)
                if (run[i] != '.') return run.Substring(0, 1); // mixed or spaced = a seam
            return run.Length >= 3 ? "..." : run.Substring(0, 1);
        }

        /// <summary>True when a speech target exists (screen reader or SAPI).</summary>
        public static bool IsAvailable => _available;

        public static string DetectedReader { get; private set; }

        public static void Init()
        {
            try
            {
                Tolk.Tolk_TrySAPI(true);
                Tolk.Tolk_PreferSAPI(false);
                Tolk.Tolk_Load();
                _loaded = Tolk.Tolk_IsLoaded();
                _available = _loaded && Tolk.Tolk_HasSpeech();
                DetectedReader = _loaded ? Tolk.DetectScreenReader() : null;
                Plugin.Logger.LogInfo($"[Speech:init] Tolk loaded={_loaded} speech={_available} reader={DetectedReader ?? "(sapi/none)"}");
            }
            catch (DllNotFoundException)
            {
                Plugin.Logger.LogError("[Speech:init] Tolk.dll not found beside the game executable — speech disabled, logging only.");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[Speech:init] " + e.Message);
            }
        }

        public static void Shutdown()
        {
            if (_loaded)
            {
                try { Tolk.Tolk_Unload(); } catch { }
            }
        }

        /// <summary>Pump the queue: speak the next queued announcement once the reader is free.</summary>
        public static void Tick()
        {
            if (Queue.Count == 0) return;
            if (Time.unscaledTime - _lastSpokeAt < Timing.SpeechQueueGap) return;
            try
            {
                if (_loaded && Tolk.Tolk_IsSpeaking()) return;
            }
            catch { }
            var next = Queue.Dequeue();
            Emit(next.Text, interrupt: false, next.Source);
        }

        /// <summary>Convenience: immediate (interrupting) speech — this codebase's
        /// dominant call shape.</summary>
        public static void Say(string text, string source) => Say(text, Priority.Immediate, source);

        /// <summary>Convenience: queued speech.</summary>
        public static void SayQueued(string text, string source) => Say(text, Priority.Queued, source);

        public static void Say(string text, Priority priority, string source)
        {
            text = Clean(text);
            if (string.IsNullOrEmpty(text)) return;

            // Modal primacy (owner design 2026-07-26, CS2): while a modal surface
            // holds the audio, only its own source speaks live. Volatile sources
            // drop; everything else is held latest-per-source and uttered after
            // release. Durable is the DEFAULT so an unregistered future source can
            // never be silently lost.
            if (_modalSource != null && source != _modalSource)
            {
                if (!VolatileSources.Contains(source))
                    PenKeepLatest(new Pending { Text = text, Source = source });
                return;
            }

            if (priority == Priority.Immediate)
            {
                // Interrupt current speech but preserve queued lines — queue flushing
                // is an explicit act, not a side effect of speaking.
                Emit(text, interrupt: true, source);
            }
            else
            {
                if (text == _lastQueued) return;
                foreach (var p in Queue)
                    if (p.Text == text) return;
                if (Queue.Count >= 20)
                {
                    Plugin.Logger.LogWarning($"[Speech:{source}] queue full, dropping: {text}");
                    return;
                }
                _lastQueued = text;
                Queue.Enqueue(new Pending { Text = text, Source = source });
                Plugin.Logger.LogInfo($"[Speech:{source}] [f{Time.frameCount}] (queued) {text}");
            }
        }

        /// <summary>Pending queued announcements (diagnostics / bridge).</summary>
        public static int QueueDepth => Queue.Count;

        /// <summary>Tail of the spoken history, oldest first (bridge /speech).</summary>
        public static List<string> RecentHistory(int max)
        {
            int start = Mathf.Max(0, History.Count - max);
            return History.GetRange(start, History.Count - start);
        }

        /// <summary>Drop pending queued announcements (used when the user navigates away).</summary>
        public static void FlushQueue()
        {
            Queue.Clear();
            _lastQueued = null;
        }

        // ---------- Modal primacy gate (owner design 2026-07-26, CS2) ----------

        private static string _modalSource;
        private static readonly HashSet<string> VolatileSources = new HashSet<string>();
        private static readonly List<Pending> Pen = new List<Pending>();

        /// <summary>Mark a source's lines as regenerating (focus, nav): under a modal
        /// they drop instead of holding, because dismissal re-fires them fresh.
        /// Registration happens in the composition root — this tier stays game-agnostic.</summary>
        public static void RegisterVolatile(string source) => VolatileSources.Add(source);

        /// <summary>A modal surface claims the audio. Pending queued lines from durable
        /// sources are preserved into the holding pen (latest per source); the rest of
        /// the queue is killed. Chained modals keep the pen: it flushes only on release.</summary>
        public static void BeginModal(string source)
        {
            _modalSource = source;
            foreach (var p in Queue)
                if (p.Source != source && !VolatileSources.Contains(p.Source))
                    PenKeepLatest(p);
            Queue.Clear();
            _lastQueued = null;
            try { if (_loaded) Tolk.Tolk_Silence(); } catch { }
        }

        /// <summary>Release the modal claim and utter the held lines, in arrival order,
        /// through the normal queued path (its dedupe applies).</summary>
        public static void EndModal()
        {
            if (_modalSource == null) return;
            _modalSource = null;
            if (Pen.Count == 0) return;
            var held = new List<Pending>(Pen);
            Pen.Clear();
            foreach (var p in held)
                Say(p.Text, Priority.Queued, p.Source);
        }

        private static void PenKeepLatest(Pending p)
        {
            for (int i = 0; i < Pen.Count; i++)
                if (Pen[i].Source == p.Source) { Pen.RemoveAt(i); break; }
            Pen.Add(p);
        }

        /// <summary>Remove and RETURN queued entries from one source, in queue
        /// order, preserving the rest — for callers that hold content across a
        /// precedence window instead of dropping it (owner doctrine
        /// 2026-08-17: precedence is a hold-and-flush, never a loss).</summary>
        public static List<string> ExtractSource(string source)
        {
            var extracted = new List<string>();
            if (Queue.Count == 0) return extracted;
            var keep = new List<Pending>();
            foreach (var p in Queue)
            {
                if (p.Source == source) extracted.Add(p.Text);
                else keep.Add(p);
            }
            Queue.Clear();
            foreach (var p in keep) Queue.Enqueue(p);
            return extracted;
        }

        /// <summary>Drop only queued entries from one source, preserving the rest.</summary>
        public static void FlushSource(string source)
        {
            if (Queue.Count == 0) return;
            var keep = new List<Pending>();
            foreach (var p in Queue)
                if (p.Source != source) keep.Add(p);
            Queue.Clear();
            foreach (var p in keep) Queue.Enqueue(p);
        }

        public static void Stop()
        {
            FlushQueue();
            try { if (_loaded) Tolk.Tolk_Silence(); } catch { }
        }

        private static void Emit(string text, bool interrupt, string source)
        {
            _lastSpokeAt = Time.unscaledTime;
            History.Add(text);
            if (History.Count > HistoryCapacity)
                History.RemoveRange(0, History.Count - HistoryCapacity);
            _historyCursor = -1; // new speech returns the browse cursor to the live end

            Plugin.Logger.LogInfo($"[Speech:{source}] [f{Time.frameCount}] {text}");
            try
            {
                if (_loaded) Tolk.Tolk_Output(text, interrupt);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[Speech:err] " + e.Message);
            }
        }

        public static void RepeatLast()
        {
            if (History.Count == 0) { Say("No speech history.", Priority.Immediate, "history"); return; }
            SpeakRaw(History[History.Count - 1]);
        }

        // ---------- History browse (SKALD [ ] keys) ----------
        // Reads the ring without re-adding, so browsing never mutates history —
        // the foundered-era self-corruption bug is impossible by construction.

        public static void HistoryPrevious()
        {
            if (History.Count == 0) { SpeakRaw("No speech history."); return; }
            if (_historyCursor == -1) _historyCursor = History.Count - 1;
            else if (_historyCursor > 0) _historyCursor--;
            SpeakRaw(History[_historyCursor]);
        }

        public static void HistoryNext()
        {
            if (History.Count == 0) { SpeakRaw("No speech history."); return; }
            if (_historyCursor == -1 || _historyCursor >= History.Count - 1)
            {
                _historyCursor = History.Count - 1;
                SpeakRaw(History[_historyCursor]);
                return;
            }
            _historyCursor++;
            SpeakRaw(History[_historyCursor]);
        }

        private static void SpeakRaw(string text)
        {
            try { if (_loaded) Tolk.Tolk_Output(text, true); } catch { }
            Plugin.Logger.LogInfo("[Speech:history] " + text);
        }

        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            text = text.Replace('�', '\'');
            text = TagPattern.Replace(text, " ");
            text = text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            text = StutterPattern.Replace(text, StutterEvaluator);
            text = SpacePattern.Replace(text, " ").Trim();
            // Leading slash-run decoration is visual styling, not content — the
            // transcode rule applied centrally. Mid-text slashes ("3 / 5") untouched.
            if (text.StartsWith("/")) text = text.TrimStart('/', ' ');
            return text.Length == 0 ? null : text;
        }
    }
}

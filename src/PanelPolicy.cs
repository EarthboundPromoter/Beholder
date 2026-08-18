using System;
using BepInEx.Configuration;

namespace SkaldAccessibility
{
    /// <summary>
    /// TP1: the describer-panel policy layer (text-surface-audit §7).
    ///
    /// Owns three things the Pump drain and the review buffer consult:
    ///
    /// 1. STRIP PROVENANCE — the always-on time/position/weather strip is
    ///    identified by RECOMPOSITION, never by string-sniffing: the game has
    ///    exactly one author for it (DataControl.getBuffer, a pure read), so
    ///    at the drain we ask the author and compare raw-to-raw. Equality =
    ///    pure strip (routed to the fact differ, never spoken whole, never
    ///    stomps the review panel); prefix = a bump-append composite (the
    ///    residue is the meaningful event text). A shape change in a game
    ///    patch cannot break this — we call the method, not a memory of it.
    ///
    /// 2. THE FACT DIFFER — the strip decomposes into four facts (time/day,
    ///    X/Y position, weather sentence, day-phase word), each diffed against
    ///    its own last-spoken record with its own config toggle (the
    ///    composed-surface idiom). Defaults per the owner ruling: position and
    ///    clock silent (pure step echo), weather and phase transitions speak
    ///    (rare, rendered, navigationally real). First observation seeds
    ///    silently. Everything queued, never interrupting.
    ///
    /// 3. THE ONE CONFIG — Panel.AutoReadBody (default ON = today's
    ///    behavior): for tooltips and UI-nav-initiated populations, off means
    ///    the identity line speaks and the body waits in the buffer.
    /// </summary>
    internal static class PanelPolicy
    {
        private static ConfigEntry<bool> _cfgAutoReadBody;
        private static ConfigEntry<bool> _cfgWeather;
        private static ConfigEntry<bool> _cfgPhase;
        private static ConfigEntry<bool> _cfgClock;
        private static ConfigEntry<bool> _cfgPosition;

        internal static void BindConfig(ConfigFile config)
        {
            _cfgAutoReadBody = config.Bind("Panel", "AutoReadBody", true,
                "Auto-read the full body of tooltips and UI-navigation-driven panel populations. "
                + "Off: only the identity line speaks; the body stays navigable in the review buffer.");
            _cfgWeather = config.Bind("Overland", "SpeakWeatherChange", true,
                "Speak the weather sentence when it changes.");
            _cfgPhase = config.Bind("Overland", "SpeakDayPhaseChange", true,
                "Speak the day-phase word (Dawn/Day/Dusk/Night) when it changes.");
            _cfgClock = config.Bind("Overland", "SpeakClockChange", false,
                "Speak time/day changes from the status strip (noisy: the clock advances every step).");
            _cfgPosition = config.Bind("Overland", "SpeakPositionChange", false,
                "Speak X/Y position changes from the status strip (noisy: changes every step).");
        }

        internal static bool AutoReadBody => _cfgAutoReadBody?.Value ?? true;

        // ---- Tag injection (the composer never reflects) ----

        private static bool _tagsInjected;
        internal static void EnsureTags()
        {
            if (_tagsInjected) return;
            _tagsInjected = true;
            Composer.PanelTags.Header = Seams.TagValue(Seams.C64_HeaderTag);
            Composer.PanelTags.AttrName = Seams.TagValue(Seams.C64_AttributeNameTag);
            Composer.PanelTags.AttrValue = Seams.TagValue(Seams.C64_AttributeValueTag);
            Composer.PanelTags.Green = Seams.TagValue(Seams.C64_GreenLightTag);
            Composer.PanelTags.Red = Seams.TagValue(Seams.C64_RedLightTag);
            if (Composer.PanelTags.Header == null || Composer.PanelTags.AttrValue == null)
                Plugin.Logger?.LogWarning("[Panel] Markup tags incomplete — sectioning degrades to paragraphs "
                    + $"(header={Composer.PanelTags.Header != null} attrValue={Composer.PanelTags.AttrValue != null})");
        }

        // ---- Live strip read (the author, asked at the drain clock) ----

        private static int _stripFrame = -1;
        private static string _stripRaw;

        /// <summary>The game's own strip composition, this frame. Pure reads
        /// (Calendar, position, weather) through the game's own composer;
        /// cached per frame; null when unavailable or empty (no map).</summary>
        internal static string LiveStripRaw()
        {
            int frame = UnityEngine.Time.frameCount;
            if (frame == _stripFrame) return _stripRaw;
            _stripFrame = frame;
            _stripRaw = null;
            try
            {
                if (Seams.MainControl_getDataControl == null || Seams.DataControl_getBuffer == null) return null;
                object dc = Seams.MainControl_getDataControl.Invoke(null, null);
                if (dc == null) return null;
                string raw = Seams.DataControl_getBuffer.Invoke(dc, null) as string;
                _stripRaw = string.IsNullOrWhiteSpace(raw) ? null : raw;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[Panel:strip] {ex.Message}");
            }
            return _stripRaw;
        }

        /// <summary>Live strip facts for the review buffer's standing status
        /// section — composed at keypress, never cached (the WP10 lazy idiom).</summary>
        internal static Composer.StripFacts LiveFacts()
        {
            EnsureTags();
            return Composer.ParseStrip(LiveStripRaw());
        }

        // ---- Strip identification + fact differ ----

        /// <summary>The last raw the author was seen to have produced — covers
        /// the cross-frame composite (a bump appends "You see: X" onto a strip
        /// baked at the previous step; no time has passed, but hold the record
        /// rather than trust that).</summary>
        private static string _lastIdentifiedStrip;

        /// <summary>Provenance test for a SecondaryDesc value. Returns true when
        /// the value is the strip or strip-prefixed; <paramref name="residueRaw"/>
        /// carries the meaningful remainder (null for a pure strip). On true the
        /// facts have already been diffed and spoken per policy.</summary>
        internal static bool TryHandleStrip(string raw, out string residueRaw)
        {
            residueRaw = null;
            if (string.IsNullOrEmpty(raw)) return false;
            string live = LiveStripRaw();

            string matched = null;
            if (live != null && raw.StartsWith(live, StringComparison.Ordinal)) matched = live;
            else if (_lastIdentifiedStrip != null
                     && raw.StartsWith(_lastIdentifiedStrip, StringComparison.Ordinal)) matched = _lastIdentifiedStrip;
            if (matched == null) return false;

            _lastIdentifiedStrip = matched;
            DiffFacts(matched);
            if (raw.Length > matched.Length)
            {
                string rest = raw.Substring(matched.Length);
                if (!string.IsNullOrWhiteSpace(rest)) residueRaw = rest;
            }
            return true;
        }

        private static string _lastClock;    // time+day combined
        private static string _lastPosition; // x+y combined
        private static string _lastWeather;
        private static string _lastPhase;

        private static void DiffFacts(string stripRaw)
        {
            EnsureTags();
            var f = Composer.ParseStrip(stripRaw);
            if (!f.Valid) return;

            string clock = (f.Time ?? "") + " " + (f.Day ?? "");
            string position = (f.X ?? "") + " " + (f.Y ?? "");

            bool first = _lastClock == null && _lastPosition == null
                         && _lastWeather == null && _lastPhase == null;

            if (!first)
            {
                if (_cfgClock?.Value == true && clock != _lastClock && f.Time != null)
                    Scaffold.SpeechService.SayQueued(Composer.EnsurePeriod(f.Time + ", " + f.Day), "Strip");
                if (_cfgPosition?.Value == true && position != _lastPosition && f.X != null)
                    Scaffold.SpeechService.SayQueued(Composer.EnsurePeriod(f.X + ", " + f.Y), "Strip");
                if (_cfgWeather?.Value == true && f.Weather != null && f.Weather != _lastWeather
                    && _lastWeather != null)
                    Scaffold.SpeechService.SayQueued(Composer.EnsurePeriod(f.Weather), "Strip");
                if (_cfgPhase?.Value == true && f.Phase != null && f.Phase != _lastPhase
                    && _lastPhase != null)
                    Scaffold.SpeechService.SayQueued(Composer.EnsurePeriod(f.Phase), "Strip");
            }

            _lastClock = clock;
            _lastPosition = position;
            if (f.Weather != null) _lastWeather = f.Weather;
            if (f.Phase != null) _lastPhase = f.Phase;
        }
    }
}

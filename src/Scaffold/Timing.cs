namespace SkaldAccessibility.Scaffold
{
    /// <summary>All tuned delays live here, with provenance (lineage discipline —
    /// one registry, per-constant justification). Ported from Sleeptalker (WP2).</summary>
    internal static class Timing
    {
        /// <summary>Minimum gap between queued utterances. CS1/CS2-proven value:
        /// short enough to feel continuous, long enough that Tolk_IsSpeaking has
        /// settled after the previous emit.</summary>
        public const float SpeechQueueGap = 0.15f;
    }
}

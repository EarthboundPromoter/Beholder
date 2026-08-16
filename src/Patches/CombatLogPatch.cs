using HarmonyLib;
using System;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Speaks combat log entries as they are written.
    ///
    /// CombatLog maintains a static text buffer and never routes through
    /// UITextBlock.setContent(), so no rendered-text hook can catch it.
    /// This patch hooks addEntry(string name, string content) — the single
    /// write point for all combat events — and speaks each entry immediately.
    ///
    /// Format: "{name}: {content}" when a source name is present (e.g. "ALDRIC: misses"),
    /// or just "{content}" when name is empty (e.g. system messages).
    /// </summary>
    [HarmonyPatch(typeof(CombatLog), "addEntry", new[] { typeof(string), typeof(string) })]
    public static class CombatLogPatch
    {
        [HarmonyPostfix]
        static void Postfix(string __0, string __1)
        {
            try
            {
                // Note-only (WP5): lines batch at the Pump and coalesce at the
                // drain, where the in-combat gate is a LIVE read of the game's own
                // current state — the mod-side IsInCombat flag no longer gates this.
                string name    = string.IsNullOrWhiteSpace(__0) ? null : TextCleaner.CleanText(__0);
                string message = string.IsNullOrWhiteSpace(__1) ? null : TextCleaner.CleanText(__1);

                string combined = (name != null && message != null)
                    ? $"{name}: {message}"
                    : name ?? message;

                if (string.IsNullOrWhiteSpace(combined)) return;

                Pump.NoteCombatLog(combined);
                Plugin.Logger?.LogInfo($"[CombatLog] \"{combined}\"");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[CombatLog] {ex.Message}");
            }
        }
    }
}

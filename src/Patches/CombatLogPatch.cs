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
                if (!GameStateTracker.IsInCombat) return;

                string name    = string.IsNullOrWhiteSpace(__0) ? null : TextCleaner.CleanText(__0);
                string message = string.IsNullOrWhiteSpace(__1) ? null : TextCleaner.CleanText(__1);

                string combined = (name != null && message != null)
                    ? $"{name}: {message}"
                    : name ?? message;

                if (string.IsNullOrWhiteSpace(combined)) return;

                Scaffold.SpeechService.Say(combined, "CombatLog");
                Plugin.Logger?.LogInfo($"[CombatLog] \"{combined}\"");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[CombatLog] {ex.Message}");
            }
        }
    }
}

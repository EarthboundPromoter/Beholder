using HarmonyLib;
using System;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Speaks combat log entries as they are written.
    ///
    /// CombatLog maintains a static text buffer and never routes through
    /// UITextBlock.setContent(), so no rendered-text hook can catch it.
    /// This patch hooks addEntry(string name, string content) — the single
    /// write point for all combat events. Seam-gated (WP8).
    ///
    /// Format: "{name}: {content}" when a source name is present (e.g. "ALDRIC: misses"),
    /// or just "{content}" when name is empty (e.g. system messages).
    /// </summary>
    [HarmonyPatch]
    public static class CombatLogPatch
    {
        [HarmonyPrepare]
        static bool Prepare() => Seams.CombatLog_addEntry != null;

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.CombatLog_addEntry;

        [HarmonyPostfix]
        static void Postfix(string __0, string __1)
        {
            try
            {
                // Note-only (WP5): lines batch at the Pump and coalesce at the
                // drain, where the in-combat gate is a LIVE read of the game's own
                // current state — never a mod-side mode flag.
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

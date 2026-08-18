using HarmonyLib;
using System;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Notes combat log entries as they are written.
    ///
    /// CombatLog maintains a static text buffer and never routes through
    /// UITextBlock.setContent(), so no rendered-text hook can catch it.
    /// This patch hooks addEntry(string, string) — the single write point for
    /// all combat events — whose two arguments are (SHORT verdict, VERBOSE
    /// ledger), NOT (name, content): every SkaldActionResult funnel passes
    /// getResultString()/getVerboseResultString() (CombatLog.cs:46-66) and
    /// the damage path calls it directly the same way (Character.cs:6134) —
    /// survey §4c.12, corrected 2026-08-18 (the old "{name}: {content}"
    /// format was unknowingly speaking the full modifier ledger per attack).
    ///
    /// Only the SHORT notes: it is the anchor fact the CP2 composer rebuilds
    /// with lettered display names; the verbose body stays in the game's own
    /// combat log — there is no mod verbose tier (owner ruling 2026-08-18).
    /// Seam-gated (WP8).
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
                // Note-only (WP5): shorts batch at the Pump; the in-combat
                // gate and all composition live at the drain.
                string shortText = string.IsNullOrWhiteSpace(__0) ? null : TextCleaner.CleanText(__0);
                if (string.IsNullOrWhiteSpace(shortText)) return;

                Pump.NoteCombatLog(shortText);
                Plugin.Logger?.LogInfo($"[CombatLog] \"{shortText}\" (verbose {(__1?.Length ?? 0)} ch stays in the log)");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[CombatLog] {ex.Message}");
            }
        }
    }
}

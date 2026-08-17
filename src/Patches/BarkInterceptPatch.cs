using HarmonyLib;
using System;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Intercepts floating text/speech bubbles created via BarkControl.Bark constructor.
    /// These are transient positional text elements (NPC speech, combat barks, etc.)
    /// Seam-gated (WP8).
    /// </summary>
    [HarmonyPatch]
    public static class BarkInterceptPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            if (Seams.Bark_ctor == null)
            {
                Plugin.Logger?.LogError("[BarkIntercept] Bark constructor seam missing — barks unvoiced");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.Bark_ctor;

        [HarmonyPostfix]
        static void Postfix(string __0, int __1, int __2)
        {
            try
            {
                if (string.IsNullOrEmpty(__0)) return;

                string cleaned = TextCleaner.CleanText(__0);
                if (string.IsNullOrWhiteSpace(cleaned)) return;

                // Note-only (WP5): barks batch at the Pump; identical repeats in a
                // frame coalesce to "text, N times" at the drain (compress, don't
                // curate), then queue — floating text never interrupts.
                Pump.NoteBark(cleaned);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[BarkIntercept] {ex.Message}");
            }
        }
    }
}

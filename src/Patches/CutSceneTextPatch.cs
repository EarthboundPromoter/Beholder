using HarmonyLib;
using System;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Cutscene text: CutSceneControl's nested renderers write their
    /// UITextBlocks directly in their constructors, so no GUIControl choke
    /// ever sees the text. One base ctor (CutsceneTextHeader) carries both the
    /// animated cutscenes' narration headers and the script-driven text cards
    /// ("Two Weeks Earlier" after the ship intro — the owner's find,
    /// 2026-08-29); CutSceneGameWinPatch below covers the game-win screen's
    /// self-composed block. Seam-gated (nested-private via GetNestedType).
    /// </summary>
    [HarmonyPatch]
    public static class CutSceneHeaderPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            if (Seams.CutsceneTextHeader_ctor == null)
            {
                Plugin.Logger?.LogError("[CutSceneText] header ctor seam missing — cutscene text unvoiced");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.CutsceneTextHeader_ctor;

        [HarmonyPostfix]
        static void Postfix(string __0)
        {
            try
            {
                if (string.IsNullOrEmpty(__0)) return;

                string cleaned = TextCleaner.CleanText(__0);
                if (string.IsNullOrWhiteSpace(cleaned)) return;

                Pump.NoteCutSceneText(cleaned);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[CutSceneText] {ex.Message}");
            }
        }
    }

    [HarmonyPatch]
    public static class CutSceneGameWinPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            if (Seams.CutSceneGameWin_ctor == null || Seams.CutSceneGameWin_textBlock == null
                || Seams.UITextBlock_content == null)
            {
                Plugin.Logger?.LogError("[CutSceneText] game-win seam missing — win text unvoiced");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.CutSceneGameWin_ctor;

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            try
            {
                object block = Seams.CutSceneGameWin_textBlock.GetValue(__instance);
                if (block == null) return;

                string raw = Seams.UITextBlock_content.GetValue(block) as string;
                if (string.IsNullOrEmpty(raw)) return;

                string cleaned = TextCleaner.CleanText(raw);
                if (string.IsNullOrWhiteSpace(cleaned)) return;

                Pump.NoteCutSceneText(cleaned);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[CutSceneText] {ex.Message}");
            }
        }
    }
}

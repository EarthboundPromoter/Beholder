using HarmonyLib;
using System;
using System.Collections.Generic;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Intercepts book content display (ItemBook.getContent).
    /// Books return List&lt;string&gt; — pages of text.
    /// We speak the content as it's retrieved for display.
    /// </summary>
    [HarmonyPatch(typeof(ItemBook), "getContent")]
    public static class BookInterceptPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ItemBook __instance, ref object __result)
        {
            try
            {
                if (__result == null) return;

                var pages = __result as List<string>;
                if (pages == null || pages.Count == 0) return;

                // Announce book opening and first page
                Plugin.Speech?.Speak($"Book opened. {pages.Count} pages.", "Book");

                // Speak first page content
                string firstPage = TextInterceptPatch.CleanText(pages[0]);
                if (!string.IsNullOrWhiteSpace(firstPage))
                {
                    Plugin.Speech?.SpeakQueued(firstPage, "Book");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[BookIntercept] {ex.Message}");
            }
        }
    }
}

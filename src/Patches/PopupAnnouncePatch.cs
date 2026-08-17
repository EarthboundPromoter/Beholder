using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Announces popup text content when a popup appears.
    ///
    /// Hooks PopUpControl.addPopUp(PopUpBase) — the single entry point (funnel)
    /// for all 25+ popup types. Reads mainDescription, secondaryDescription, and
    /// tertiaryDescription text from the popup's UI elements. Does NOT read button
    /// labels — popup button navigation speaks via the selection join.
    /// Seam-gated (WP8).
    ///
    /// Double-speak guard (WP8): many popup constructors route their initial
    /// text through the PopUpBase set*TextContent setters (decomp-verified,
    /// e.g. PopUpLock's ctor), which ContentSpeechPatch also hooks — so popup
    /// arrival used to fire BOTH mechanisms, the second interrupting and
    /// re-speaking the first. The drain speaks arrival from here (settled
    /// field reads), then SEEDS the content diff records so the same-frame
    /// setter notes dedup away; a genuine post-arrival update still differs
    /// and still speaks.
    /// </summary>
    [HarmonyPatch]
    public static class PopupAnnouncePatch
    {
        private static bool ReadReady =>
            Seams.PopUpBase_uiElements != null
            && Seams.PopUpUIBase_mainDescription != null
            && Seams.UITextBlock_content != null;

        [HarmonyPrepare]
        static bool Prepare()
        {
            if (Seams.PopUpControl_addPopUp == null || !ReadReady)
            {
                Plugin.Logger?.LogWarning("[PopupAnnounce] addPopUp/read seams missing — popup announce disabled");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.PopUpControl_addPopUp;

        [HarmonyPostfix]
        static void Postfix(object __0)
        {
            // Note-only (WP5): the Pump drains at end of frame, so text set AFTER
            // addPopUp within the same frame is read at its settled value.
            if (__0 != null) Pump.NotePopup(__0);
        }

        /// <summary>Composition, called from the Pump's drain.</summary>
        public static void SpeakPopupTexts(object popup)
        {
            try
            {
                if (!ReadReady || popup == null) return;

                object uiElements = Seams.PopUpBase_uiElements.GetValue(popup);
                if (uiElements == null) return;

                string main = ReadDescription(Seams.PopUpUIBase_mainDescription, uiElements);
                string secondary = ReadDescription(Seams.PopUpUIBase_secondaryDescription, uiElements);
                string tertiary = ReadDescription(Seams.PopUpUIBase_tertiaryDescription, uiElements);

                // The popup's combined raw text is the review layer's panel
                // while the popup is up (WP10).
                string rawPanel = string.Join("\n\n", new[]
                {
                    ReadRaw(Seams.PopUpUIBase_mainDescription, uiElements),
                    ReadRaw(Seams.PopUpUIBase_secondaryDescription, uiElements),
                    ReadRaw(Seams.PopUpUIBase_tertiaryDescription, uiElements),
                }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray());
                if (rawPanel.Length > 0) ReviewLayer.NotePanel(rawPanel);

                // Arrival owns these values now — seed the content diff so the
                // same frame's ctor-setter notes collapse instead of re-speaking.
                if (main != null) Pump.SeedContent("PopupMain", main);
                if (secondary != null) Pump.SeedContent("PopupSecondary", secondary);
                if (tertiary != null) Pump.SeedContent("PopupTertiary", tertiary);

                // Speak the first non-empty description with interrupt; remaining
                // descriptions queue behind it.
                bool spoken = false;

                if (!string.IsNullOrWhiteSpace(main))
                {
                    Scaffold.SpeechService.Say(main, "Popup");
                    Plugin.Logger?.LogInfo($"[Popup:text] \"{main}\"");
                    spoken = true;
                }

                if (!string.IsNullOrWhiteSpace(secondary))
                {
                    if (spoken)
                    {
                        Scaffold.SpeechService.SayQueued(secondary, "Popup");
                    }
                    else
                    {
                        Scaffold.SpeechService.Say(secondary, "Popup");
                        spoken = true;
                    }
                    Plugin.Logger?.LogInfo($"[Popup:text2] \"{secondary}\"");
                }

                if (!string.IsNullOrWhiteSpace(tertiary))
                {
                    if (spoken)
                    {
                        Scaffold.SpeechService.SayQueued(tertiary, "Popup");
                    }
                    else
                    {
                        Scaffold.SpeechService.Say(tertiary, "Popup");
                    }
                    Plugin.Logger?.LogInfo($"[Popup:text3] \"{tertiary}\"");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[PopupAnnounce] {ex.Message}");
            }
        }

        private static string ReadDescription(FieldInfo field, object uiElements)
        {
            string raw = ReadRaw(field, uiElements);
            return raw == null ? null : TextCleaner.CleanText(raw);
        }

        private static string ReadRaw(FieldInfo field, object uiElements)
        {
            if (field == null) return null;
            object textBlock = field.GetValue(uiElements);
            if (textBlock == null) return null;
            string raw = Seams.UITextBlock_content.GetValue(textBlock) as string;
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
    }
}

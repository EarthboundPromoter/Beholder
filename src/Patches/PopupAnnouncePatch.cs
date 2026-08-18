using System;
using System.Linq;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Popup arrival composition, called from the Pump's top-of-stack watch
    /// (CC report build 2026-08-16 — no longer a Harmony patch).
    ///
    /// Why the watch replaced the addPopUp postfix: the game keeps a popup
    /// STACK, and the add event alone provably misses two real paths — a popup
    /// revealed by dismissing the one stacked above it (PopUpName's caution
    /// chain) never re-fires addPopUp, and PopUpSpellSelector builds its UI a
    /// frame after the add, so an add-time read sees an empty popup. The drain
    /// reads PopUpControl.getCurrentPopUp() — the game's own authoritative
    /// current-popup accessor — and announces identity changes, retrying while
    /// a popup's UI is not yet built (this method returns false).
    ///
    /// Reads mainDescription, secondaryDescription, and tertiaryDescription
    /// directly from the popup's UI elements: most popup ctors (PopUpOK,
    /// PopUpYesNo) write those blocks directly and never touch the hooked
    /// set*TextContent setters, so the block read is the only complete source.
    /// Button labels are NOT read here — popup button navigation speaks via
    /// the selection join (queued on the arrival frame so it can't cut the
    /// body). Seam-gated (WP8).
    ///
    /// Double-speak guards: arrival seeds the Popup* content diffs (ctor-setter
    /// notes from the same frame collapse — the WP8 pattern) AND the
    /// SecondaryDesc/SheetDesc diffs, because the CC intro popups push a
    /// duplicate of their message into those screen sources (addIntroPopUp,
    /// the feats intro buffer); a genuinely different later value still speaks.
    /// </summary>
    public static class PopupAnnouncePatch
    {
        private static bool ReadReady =>
            Seams.PopUpBase_uiElements != null
            && Seams.PopUpUIBase_mainDescription != null
            && Seams.UITextBlock_content != null;

        /// <summary>The popup's whole document — main + secondary + tertiary
        /// joined — for the review buffer. The tertiary's console-echo blink
        /// is normalized out so a name-entry cursor can't churn the capture
        /// (TP1 review find 2: the buffer holds the COMPOSITE; forwarding a
        /// single updated block would truncate the document to that block).</summary>
        internal static string ComposePanel(object popup)
        {
            try
            {
                if (!ReadReady || popup == null) return null;
                object uiElements = Seams.PopUpBase_uiElements.GetValue(popup);
                if (uiElements == null) return null;
                string tertiary = ReadRaw(Seams.PopUpUIBase_tertiaryDescription, uiElements);
                if (tertiary != null) tertiary = tertiary.TrimEnd('_').TrimEnd();
                string rawPanel = string.Join("\n\n", new[]
                {
                    ReadRaw(Seams.PopUpUIBase_mainDescription, uiElements),
                    ReadRaw(Seams.PopUpUIBase_secondaryDescription, uiElements),
                    tertiary,
                }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray());
                return rawPanel.Length > 0 ? rawPanel : null;
            }
            catch { return null; }
        }

        /// <summary>Announce a popup's settled text. Returns false only when
        /// the popup's UI is not yet built (retry next drain); true once
        /// handled — including degraded (seams missing) and empty popups.</summary>
        public static bool SpeakPopupTexts(object popup)
        {
            try
            {
                if (!ReadReady || popup == null) return true;

                object uiElements = Seams.PopUpBase_uiElements.GetValue(popup);
                if (uiElements == null) return false; // built next frame — retry

                string main = ReadDescription(Seams.PopUpUIBase_mainDescription, uiElements);
                string secondary = ReadDescription(Seams.PopUpUIBase_secondaryDescription, uiElements);
                string tertiary = ReadDescription(Seams.PopUpUIBase_tertiaryDescription, uiElements);

                // The popup's combined raw text is the review layer's panel
                // while the popup is up (WP10).
                string rawPanel = ComposePanel(popup);
                if (rawPanel != null) ReviewLayer.NotePanel("Popup", rawPanel);

                // Arrival owns these values now — seed the content diffs so the
                // same frame's setter notes and the intro popups' screen-source
                // duplicates collapse instead of re-speaking.
                if (main != null)
                {
                    Pump.SeedContent("PopupMain", main);
                    Pump.SeedContent("SecondaryDesc", main);
                    Pump.SeedContent("SheetDesc", main);
                }
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
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[PopupAnnounce] {ex.Message}");
                return true; // never retry-spam an unreadable popup
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

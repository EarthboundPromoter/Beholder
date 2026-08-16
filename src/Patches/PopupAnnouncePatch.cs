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
    /// labels — popup button navigation speaks via the selection join
    /// (SelectionJoinPatch + Pump), which replaced the old per-frame
    /// PopupNavigationPatch and its init-suppression window in WP4.
    /// </summary>
    [HarmonyPatch]
    public static class PopupAnnouncePatch
    {
        private static FieldInfo _uiElementsField;
        private static FieldInfo _mainDescField;
        private static FieldInfo _secondaryDescField;
        private static FieldInfo _tertiaryDescField;
        private static FieldInfo _contentField;
        private static bool _initialized;
        private static bool _initFailed;

        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("PopUpControl");
            if (type == null)
            {
                Plugin.Logger?.LogWarning("[PopupAnnounce] PopUpControl not found");
                return null;
            }
            var method = AccessTools.Method(type, "addPopUp",
                new[] { AccessTools.TypeByName("PopUpBase") });
            if (method == null)
            {
                Plugin.Logger?.LogWarning("[PopupAnnounce] addPopUp method not found");
                return null;
            }
            Plugin.Logger?.LogInfo("[PopupAnnounce] Found PopUpControl.addPopUp for patching");
            return method;
        }

        private static void Initialize()
        {
            if (_initFailed) return;
            try
            {
                var popUpBaseType = AccessTools.TypeByName("PopUpBase");
                var popUpUIBaseType = AccessTools.TypeByName("PopUpBase+PopUpUIBase");

                if (popUpBaseType == null || popUpUIBaseType == null)
                {
                    Plugin.Logger?.LogError("[PopupAnnounce] Required types not found");
                    _initFailed = true;
                    return;
                }

                _uiElementsField = AccessTools.Field(popUpBaseType, "uiElements");
                _mainDescField = AccessTools.Field(popUpUIBaseType, "mainDescription");
                _secondaryDescField = AccessTools.Field(popUpUIBaseType, "secondaryDescription");
                _tertiaryDescField = AccessTools.Field(popUpUIBaseType, "tertiaryDescription");
                _contentField = AccessTools.Field(typeof(UITextBlock), "content");

                _initialized = _uiElementsField != null
                    && _mainDescField != null
                    && _contentField != null;

                if (_initialized)
                    Plugin.Logger?.LogInfo("[PopupAnnounce] Initialized successfully");
                else
                {
                    Plugin.Logger?.LogError("[PopupAnnounce] Init failed — missing fields");
                    _initFailed = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[PopupAnnounce] Init: {ex.Message}");
                _initFailed = true;
            }
        }

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
                if (!_initialized) { if (_initFailed) return; Initialize(); if (!_initialized) return; }
                if (popup == null) return;

                object uiElements = _uiElementsField.GetValue(popup);
                if (uiElements == null) return;

                string main = ReadDescription(_mainDescField, uiElements);
                string secondary = ReadDescription(_secondaryDescField, uiElements);
                string tertiary = ReadDescription(_tertiaryDescField, uiElements);

                // The popup's combined raw text is the review layer's panel
                // while the popup is up (WP10).
                string rawPanel = string.Join("\n\n", new[]
                {
                    ReadRaw(_mainDescField, uiElements),
                    ReadRaw(_secondaryDescField, uiElements),
                    ReadRaw(_tertiaryDescField, uiElements),
                }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray());
                if (rawPanel.Length > 0) ReviewLayer.NotePanel(rawPanel);

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
            string raw = _contentField.GetValue(textBlock) as string;
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
    }
}

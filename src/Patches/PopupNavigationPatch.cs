using HarmonyLib;
using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Announces focused button text when navigating popup dialogs.
    /// Uses NavigationCursor index instead of hover observation.
    ///
    /// Covers all 25+ popup types. Init suppression lets popup body text
    /// be heard before the first button is announced. (Both the per-frame
    /// polling and the suppression window are scheduled for deletion in
    /// build-plan WP4, replaced by the selection join.)
    /// Enter-key activation is SkaldIOPatch's job (Enter maps to a click).
    /// </summary>
    [HarmonyPatch]
    public static class PopupNavigationPatch
    {
        private const int InitSuppressFrames = 30; // ~0.5s at 60fps

        // Per-instance frame counter for init suppression
        private static readonly ConditionalWeakTable<object, FrameCounter> _frames =
            new ConditionalWeakTable<object, FrameCounter>();

        private class FrameCounter { public int Count; }

        private static Type _popupButtonType;
        private static MethodInfo _getButtonsListMethod;
        private static FieldInfo _contentField;
        private static bool _initialized;
        private static bool _initFailed;

        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
        {
            var baseType = AccessTools.TypeByName("UIButtonControlBase");
            if (baseType == null)
            {
                Plugin.Logger?.LogWarning("[PopupNav] UIButtonControlBase not found");
                return null;
            }
            var method = AccessTools.Method(baseType, "update");
            if (method == null)
            {
                Plugin.Logger?.LogWarning("[PopupNav] UIButtonControlBase.update() not found");
                return null;
            }
            Plugin.Logger?.LogInfo("[PopupNav] Patching UIButtonControlBase.update()");
            return method;
        }

        private static void Initialize()
        {
            if (_initFailed) return;
            try
            {
                _popupButtonType = AccessTools.TypeByName("UIPopUpButtonControl");
                var baseType     = AccessTools.TypeByName("UIButtonControlBase");

                if (_popupButtonType == null || baseType == null)
                {
                    Plugin.Logger?.LogError("[PopupNav] Required types not found");
                    _initFailed = true;
                    return;
                }

                _getButtonsListMethod     = AccessTools.Method(baseType, "getButtonsList");
                _contentField             = AccessTools.Field(typeof(UITextBlock), "content");

                _initialized = _getButtonsListMethod != null
                    && _contentField != null;

                if (_initialized)
                    Plugin.Logger?.LogInfo("[PopupNav] Initialized successfully");
                else
                {
                    Plugin.Logger?.LogError("[PopupNav] Init failed — missing fields");
                    _initFailed = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[PopupNav] Init exception: {ex.Message}");
                _initFailed = true;
            }
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                if (!_initialized)
                {
                    if (_initFailed) return;
                    Initialize();
                    if (!_initialized) return;
                }

                if (__instance.GetType() != _popupButtonType) return;

                var frame = _frames.GetOrCreateValue(__instance);
                frame.Count++;

                int index = NavigationCursor.GetIndex(__instance);

                // Enter key activation is handled by SkaldIOPatch mapping Enter → left mouse click.
                // Clear content speech dedup when Enter is pressed so the underlying screen
                // speaks fresh content after popup dismissal.
                if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Return) && index >= 0)
                {
                    ContentSpeechPatch.ClearAll();
                    Plugin.Logger?.LogInfo($"[Popup:enter] [{index}]");
                }

                // Suppress speech during init window
                if (frame.Count <= InitSuppressFrames) return;

                // Speak if index changed
                if (index < 0) return;
                var cursorState = NavigationCursor.GetState(__instance);
                string text = ReadButtonText(__instance, index);
                if (text != null && text != cursorState.LastSpoken)
                {
                    cursorState.LastSpoken = text;
                    Plugin.Speech?.Speak(text, "PopupNav");
                    Plugin.Logger?.LogInfo($"[Nav:popup] {index} \"{text}\"");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[PopupNav] {ex.Message}");
            }
        }

        private static string ReadButtonText(object control, int index)
        {
            var buttons = _getButtonsListMethod.Invoke(control, null) as IList;
            if (buttons == null || index < 0 || index >= buttons.Count) return null;
            object button = buttons[index];
            if (button == null) return null;

            string raw = null;
            try { raw = _contentField.GetValue(button) as string; }
            catch { return null; }

            if (string.IsNullOrWhiteSpace(raw) || raw == " ") return null;

            string cleaned = TextCleaner.CleanText(raw);
            if (string.IsNullOrWhiteSpace(cleaned)) return null;
            return cleaned;
        }
    }


    /// <summary>
    /// Announces popup text content when a popup appears.
    ///
    /// Hooks PopUpControl.addPopUp(PopUpBase) — the single entry point for all
    /// 25+ popup types. Reads mainDescription, secondaryDescription, and
    /// tertiaryDescription text from the popup's UI elements. Does NOT read
    /// button labels — those are navigated via PopupHoverPatch.
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
            try
            {
                if (!_initialized) { if (_initFailed) return; Initialize(); if (!_initialized) return; }
                if (__0 == null) return;

                object uiElements = _uiElementsField.GetValue(__0);
                if (uiElements == null) return;

                string main = ReadDescription(_mainDescField, uiElements);
                string secondary = ReadDescription(_secondaryDescField, uiElements);
                string tertiary = ReadDescription(_tertiaryDescField, uiElements);

                // Speak the first non-empty description with Speak() (interrupts).
                // Remaining descriptions use SpeakQueued() (follow in sequence).
                bool spoken = false;

                if (!string.IsNullOrWhiteSpace(main))
                {
                    Plugin.Speech?.Speak(main, "Popup");
                    Plugin.Logger?.LogInfo($"[Popup:text] \"{main}\"");
                    spoken = true;
                }

                if (!string.IsNullOrWhiteSpace(secondary))
                {
                    if (spoken)
                    {
                        Plugin.Speech?.SpeakQueued(secondary, "Popup");
                    }
                    else
                    {
                        Plugin.Speech?.Speak(secondary, "Popup");
                        spoken = true;
                    }
                    Plugin.Logger?.LogInfo($"[Popup:text2] \"{secondary}\"");
                }

                if (!string.IsNullOrWhiteSpace(tertiary))
                {
                    if (spoken)
                    {
                        Plugin.Speech?.SpeakQueued(tertiary, "Popup");
                    }
                    else
                    {
                        Plugin.Speech?.Speak(tertiary, "Popup");
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
            if (field == null) return null;
            object textBlock = field.GetValue(uiElements);
            if (textBlock == null) return null;
            string raw = _contentField.GetValue(textBlock) as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return TextCleaner.CleanText(raw);
        }
    }
}

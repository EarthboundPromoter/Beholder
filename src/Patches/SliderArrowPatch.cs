using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Slider input + description (WP6). One postfix on UITextSliderControl.update:
    /// on Left/Right, reads the control's own hoverButton field — the game's
    /// authoritative slider focus, read at time of use, never a mod mirror — and
    /// dispatches the per-type backing mutation (the March-proven mutators), then
    /// notes the button so the Pump speaks the SETTLED value at end of frame.
    ///
    /// Also hosts the slider-row describer used by the Pump's selection
    /// composition ("Header: Value" + queued description) — replacing the deleted
    /// SliderHoverPatch/SliderChangePatch per-frame watchers, their 3-frame
    /// window, and the first-value swallow.
    /// </summary>
    [HarmonyPatch]
    public static class SliderArrowPatch
    {
        private static FieldInfo _hoverButtonField;
        private static FieldInfo _headerTextBlockField;
        private static FieldInfo _currentValueTextBlockField;
        private static FieldInfo _contentField;
        private static FieldInfo _settingField;
        private static MethodInfo _incrementStateMethod;
        private static FieldInfo _pointerField;
        private static MethodInfo _boundPointerMethod;
        private static FieldInfo _characterField;
        private static MethodInfo _cycleActivityMethod;
        private static readonly Dictionary<Type, MethodInfo> _descMethods = new Dictionary<Type, MethodInfo>();
        private static bool _initialized;
        private static bool _initFailed;

        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("UITextSliderControl");
            if (type == null)
            {
                Plugin.Logger?.LogWarning("[SliderArrow] UITextSliderControl not found");
                return null;
            }
            var method = AccessTools.Method(type, "update");
            if (method == null)
            {
                Plugin.Logger?.LogWarning("[SliderArrow] UITextSliderControl.update not found");
                return null;
            }
            Plugin.Logger?.LogInfo("[SliderArrow] Patching UITextSliderControl.update");
            return method;
        }

        private static void Initialize()
        {
            if (_initFailed) return;
            try
            {
                var controlType = AccessTools.TypeByName("UITextSliderControl");
                var buttonType = AccessTools.TypeByName("UITextSliderButton");
                if (controlType == null || buttonType == null) { _initFailed = true; return; }

                _hoverButtonField = AccessTools.Field(controlType, "hoverButton");
                _headerTextBlockField = AccessTools.Field(buttonType, "headerTextBlock");
                _currentValueTextBlockField = AccessTools.Field(buttonType, "currentValueTextBlock");
                _contentField = AccessTools.Field(typeof(UITextBlock), "content");

                var settingsType = AccessTools.TypeByName("UITextSliderSettingsButton");
                if (settingsType != null)
                {
                    _settingField = AccessTools.Field(settingsType, "setting");
                    if (_settingField != null)
                        _incrementStateMethod = AccessTools.Method(_settingField.FieldType, "incrementState", new[] { typeof(int) });
                }
                var customType = AccessTools.TypeByName("UITextCustomSliderButton");
                if (customType != null)
                {
                    _pointerField = AccessTools.Field(customType, "pointer");
                    _boundPointerMethod = AccessTools.Method(customType, "boundPointer");
                }
                var campType = AccessTools.TypeByName("UITextSliderCampActivityButton");
                if (campType != null)
                {
                    _characterField = AccessTools.Field(campType, "character");
                    if (_characterField != null)
                        _cycleActivityMethod = AccessTools.Method(_characterField.FieldType, "cyclePreferredCampActivity", new[] { typeof(int) });
                }

                _initialized = _hoverButtonField != null && _headerTextBlockField != null
                    && _currentValueTextBlockField != null && _contentField != null;
                if (!_initialized) { Plugin.Logger?.LogError("[SliderArrow] Init failed — missing fields"); _initFailed = true; }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[SliderArrow] Init: {ex.Message}");
                _initFailed = true;
            }
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            try
            {
                bool left = Input.GetKeyDown(KeyCode.LeftArrow);
                bool right = Input.GetKeyDown(KeyCode.RightArrow);
                if (!left && !right) return;

                if (!_initialized) { if (_initFailed) return; Initialize(); if (!_initialized) return; }

                // The game's own focus, read at time of use.
                object hoverButton = _hoverButtonField.GetValue(__instance);
                if (hoverButton == null) return;

                int direction = right ? 1 : -1;
                string typeName = hoverButton.GetType().Name;

                if (typeName == "UITextSliderSettingsButton" && _settingField != null && _incrementStateMethod != null)
                {
                    object setting = _settingField.GetValue(hoverButton);
                    if (setting != null)
                        _incrementStateMethod.Invoke(setting, new object[] { direction });
                }
                else if (typeName == "UITextCustomSliderButton" && _pointerField != null)
                {
                    int pointer = (int)_pointerField.GetValue(hoverButton);
                    _pointerField.SetValue(hoverButton, pointer + direction);
                    _boundPointerMethod?.Invoke(hoverButton, null);
                }
                else if (typeName == "UITextSliderCampActivityButton" && _characterField != null && _cycleActivityMethod != null)
                {
                    object character = _characterField.GetValue(hoverButton);
                    if (character != null)
                        _cycleActivityMethod.Invoke(character, new object[] { direction });
                }
                else
                {
                    return; // unknown slider type — graceful silence, no note
                }

                // The value text block refreshes during the game's own redraw;
                // the Pump reads the settled value at end of frame.
                Pump.NoteSliderValue(hoverButton);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[SliderArrow] {ex.Message}");
            }
        }

        // ---------- Describer (used by Pump composition and value drain) ----------

        /// <summary>"Header: Value" for a slider row (or just the value). Returns
        /// null if the object is not a slider button — callers fall through.</summary>
        public static string ReadSliderRow(object button, bool valueOnly)
        {
            try
            {
                if (!_initialized) { if (_initFailed) return null; Initialize(); if (!_initialized) return null; }
                if (_headerTextBlockField == null) return null;
                if (!_headerTextBlockField.DeclaringType.IsInstanceOfType(button)) return null;

                object headerBlock = _headerTextBlockField.GetValue(button);
                object valueBlock = _currentValueTextBlockField.GetValue(button);
                string header = headerBlock != null ? _contentField.GetValue(headerBlock) as string : null;
                string value = valueBlock != null ? _contentField.GetValue(valueBlock) as string : null;
                string cleanedHeader = string.IsNullOrWhiteSpace(header) ? null : TextCleaner.CleanText(header);
                string cleanedValue = string.IsNullOrWhiteSpace(value) ? null : TextCleaner.CleanText(value);

                if (valueOnly) return cleanedValue ?? cleanedHeader;
                if (cleanedHeader != null && cleanedValue != null) return $"{cleanedHeader}: {cleanedValue}";
                return cleanedHeader ?? cleanedValue;
            }
            catch { return null; }
        }

        /// <summary>Queue the row's full description behind the name announcement
        /// (getDescription is abstract on UITextSliderButton; MethodInfo cached
        /// per concrete type).</summary>
        public static void QueueDescription(object button)
        {
            try
            {
                var type = button.GetType();
                if (!_descMethods.TryGetValue(type, out var method))
                {
                    method = AccessTools.Method(type, "getDescription");
                    _descMethods[type] = method;
                }
                if (method == null) return;
                string desc = method.Invoke(button, null) as string;
                string cleaned = string.IsNullOrWhiteSpace(desc) ? null : TextCleaner.CleanText(desc);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    Scaffold.SpeechService.SayQueued(cleaned, "SliderDesc");
            }
            catch { }
        }
    }
}

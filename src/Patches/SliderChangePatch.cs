using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Announces the focused slider when the user navigates between sliders.
    ///
    /// UITextSliderControl.update() tracks which slider the cursor is over in its
    /// private `hoverButton` field. We watch that field for changes and speak
    /// "{header}: {value}" (e.g., "Volume: 75") when focus moves to a new slider.
    ///
    /// This covers Up/Down arrow navigation in Settings, Camping, and Appearance.
    /// Value changes (Left/Right arrow) are handled by SliderChangePatch below.
    /// </summary>
    [HarmonyPatch]
    public static class SliderHoverPatch
    {
        private const int InitSuppressFrames = 3;

        private class InstanceState
        {
            public object LastHoverButton;
            public int FrameCount;
        }

        private static readonly ConditionalWeakTable<object, InstanceState> _states =
            new ConditionalWeakTable<object, InstanceState>();

        /// <summary>
        /// The last slider button that was hovered. Used by SliderArrowPatch to
        /// target Left/Right value changes even when the cursor has moved away
        /// (e.g., popup navigation moving cursor to buttons).
        /// </summary>
        internal static object LastFocusedSliderButton;

        private static FieldInfo _hoverButtonField;
        private static FieldInfo _headerTextBlockField;
        private static FieldInfo _currentValueTextBlockField;
        private static FieldInfo _contentField;
        private static bool _initialized;
        private static bool _initFailed;

        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("UITextSliderControl");
            if (type == null)
            {
                Plugin.Logger?.LogWarning("[SliderHover] UITextSliderControl not found");
                return null;
            }
            var method = AccessTools.Method(type, "update");
            if (method == null)
            {
                Plugin.Logger?.LogWarning("[SliderHover] UITextSliderControl.update() not found");
                return null;
            }
            Plugin.Logger?.LogInfo("[SliderHover] Found UITextSliderControl.update() for patching");
            return method;
        }

        private static void Initialize()
        {
            if (_initFailed) return;
            try
            {
                var controlType = AccessTools.TypeByName("UITextSliderControl");
                var buttonType  = AccessTools.TypeByName("UITextSliderButton");
                if (controlType == null || buttonType == null)
                {
                    _initFailed = true;
                    return;
                }

                _hoverButtonField           = AccessTools.Field(controlType, "hoverButton");
                _headerTextBlockField       = AccessTools.Field(buttonType,  "headerTextBlock");
                _currentValueTextBlockField = AccessTools.Field(buttonType,  "currentValueTextBlock");
                _contentField               = AccessTools.Field(typeof(UITextBlock), "content");

                _initialized = _hoverButtonField != null
                    && _headerTextBlockField != null
                    && _currentValueTextBlockField != null
                    && _contentField != null;

                if (_initialized)
                    Plugin.Logger?.LogInfo("[SliderHover] Initialized successfully");
                else
                {
                    Plugin.Logger?.LogError("[SliderHover] Init failed — missing fields");
                    _initFailed = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[SliderHover] Init exception: {ex.Message}");
                _initFailed = true;
            }
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            try
            {
                if (!_initialized)
                {
                    if (_initFailed) return;
                    Initialize();
                    if (!_initialized) return;
                }

                object hoverButton = _hoverButtonField.GetValue(__instance);

                // Check null BEFORE updating state. UITextSliderControl.update() can
                // clear hoverButton to null momentarily (e.g. at list edges), which would
                // reset LastHoverButton and cause the same slider to re-announce when focus
                // returns. We simply ignore null — it means "no slider focused".
                if (hoverButton == null) return;

                var state = _states.GetOrCreateValue(__instance);

                // Count every frame for this instance. The first InitSuppressFrames
                // frames are silenced to absorb the init burst when a slider control
                // first appears (screen load or popup open).
                state.FrameCount++;

                if (ReferenceEquals(hoverButton, state.LastHoverButton)) return;
                state.LastHoverButton = hoverButton;
                LastFocusedSliderButton = hoverButton;

                if (state.FrameCount <= InitSuppressFrames)
                {
                    Plugin.Logger?.LogDebug($"[SliderHover] init-suppress frame {state.FrameCount}");
                    return;
                }

                object headerBlock = _headerTextBlockField.GetValue(hoverButton);
                object valueBlock  = _currentValueTextBlockField.GetValue(hoverButton);

                string header = headerBlock != null ? _contentField.GetValue(headerBlock) as string : null;
                string value  = valueBlock  != null ? _contentField.GetValue(valueBlock)  as string : null;

                string cleanedHeader = string.IsNullOrWhiteSpace(header) ? null : TextInterceptPatch.CleanText(header);
                string cleanedValue  = string.IsNullOrWhiteSpace(value)  ? null : TextInterceptPatch.CleanText(value);

                string announcement = cleanedHeader != null && cleanedValue != null
                    ? $"{cleanedHeader}: {cleanedValue}"
                    : cleanedHeader ?? cleanedValue;

                if (!string.IsNullOrWhiteSpace(announcement))
                {
                    Plugin.Speech?.Speak(announcement, "SliderHov");
                    Plugin.Logger?.LogInfo($"[Nav:slider:hov] \"{announcement}\"");
                }

                // Speak the slider's full description text.
                // FIX: use SpeakQueued so the description follows the name announcement
                // instead of cancelling it. Speak() calls cancelSpeech() before each
                // utterance, which would cut off the name before the user hears it.
                // getDescription() is abstract on UITextSliderButton; subclasses return
                // setting.getFullDescriptionAndHeader(), activity description, etc.
                try
                {
                    var descMethod = AccessTools.Method(hoverButton.GetType(), "getDescription");
                    if (descMethod != null)
                    {
                        string desc = descMethod.Invoke(hoverButton, null) as string;
                        string cleanedDesc = string.IsNullOrWhiteSpace(desc)
                            ? null
                            : TextInterceptPatch.CleanText(desc);
                        if (!string.IsNullOrWhiteSpace(cleanedDesc))
                        {
                            Plugin.Speech?.SpeakQueued(cleanedDesc, "SliderDesc");
                            Plugin.Logger?.LogInfo($"[Nav:slider:desc] \"{cleanedDesc}\"");
                        }
                    }
                }
                catch (Exception descEx)
                {
                    Plugin.Logger?.LogDebug($"[SliderHover:desc] {descEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[SliderHover] {ex.Message}");
            }
        }
    }


    /// <summary>
    /// Announces slider value changes for UITextSliderButton subclasses.
    ///
    /// UITextSliderButton extends UICanvasVertical (not UIButtonControlBase), so
    /// ButtonHoverPatch cannot reach these controls. They are used in:
    ///   Settings screens       — UITextSliderSettingsButton
    ///   Appearance editor      — UITextCustomSliderButton
    ///   Camping activity picks — UITextSliderCampActivityButton
    ///
    /// On each value change, speaks "{header}: {value}" (e.g., "Volume: 75").
    /// Initial load values are silenced — only user-driven changes are announced.
    /// </summary>
    [HarmonyPatch]
    public static class SliderChangePatch
    {
        private class SliderState
        {
            public string LastValue;
            // Suppress the very first announcement (screen-load initial value dump)
            public bool HasInitialized;
        }

        private static readonly ConditionalWeakTable<object, SliderState> _states =
            new ConditionalWeakTable<object, SliderState>();

        private static FieldInfo _headerTextBlockField;
        private static FieldInfo _currentValueTextBlockField;
        private static FieldInfo _contentField;
        private static bool _initialized;
        private static bool _initFailed;

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> TargetMethods()
        {
            // HashSet deduplicates in case multiple type names resolve to the same base method
            var seen = new HashSet<MethodBase>();

            foreach (var typeName in new[]
            {
                "UITextCustomSliderButton",
                "UITextSliderSettingsButton",
                "UITextSliderCampActivityButton"
            })
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    Plugin.Logger?.LogWarning($"[SliderChange] {typeName} not found");
                    continue;
                }
                var method = AccessTools.Method(type, "update");
                if (method == null)
                {
                    Plugin.Logger?.LogWarning($"[SliderChange] {typeName}.update() not found");
                    continue;
                }
                if (seen.Add(method))
                {
                    Plugin.Logger?.LogInfo($"[SliderChange] Patching {method.DeclaringType?.Name}.update()");
                    yield return method;
                }
            }
        }

        private static void Initialize()
        {
            if (_initFailed) return;
            try
            {
                var sliderType = AccessTools.TypeByName("UITextSliderButton");
                if (sliderType == null)
                {
                    Plugin.Logger?.LogError("[SliderChange] UITextSliderButton not found");
                    _initFailed = true;
                    return;
                }

                _headerTextBlockField = AccessTools.Field(sliderType, "headerTextBlock");
                _currentValueTextBlockField = AccessTools.Field(sliderType, "currentValueTextBlock");
                _contentField = AccessTools.Field(typeof(UITextBlock), "content");

                _initialized = _headerTextBlockField != null
                    && _currentValueTextBlockField != null
                    && _contentField != null;

                if (_initialized)
                    Plugin.Logger?.LogInfo("[SliderChange] Initialized successfully");
                else
                {
                    Plugin.Logger?.LogError("[SliderChange] Init failed — missing fields");
                    _initFailed = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[SliderChange] Init exception: {ex.Message}");
                _initFailed = true;
            }
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            try
            {
                if (!_initialized)
                {
                    if (_initFailed) return;
                    Initialize();
                    if (!_initialized) return;
                }

                object headerBlock = _headerTextBlockField.GetValue(__instance);
                object valueBlock = _currentValueTextBlockField.GetValue(__instance);
                if (headerBlock == null || valueBlock == null) return;

                string value = _contentField.GetValue(valueBlock) as string;
                if (string.IsNullOrWhiteSpace(value)) return;

                string cleanedValue = TextInterceptPatch.CleanText(value);
                if (string.IsNullOrWhiteSpace(cleanedValue)) return;

                var state = _states.GetOrCreateValue(__instance);

                if (!state.HasInitialized)
                {
                    // Record initial value without speaking — suppresses screen-load dump
                    state.LastValue = cleanedValue;
                    state.HasInitialized = true;
                    return;
                }

                if (cleanedValue == state.LastValue) return;
                state.LastValue = cleanedValue;

                string header = _contentField.GetValue(headerBlock) as string;
                string cleanedHeader = string.IsNullOrWhiteSpace(header)
                    ? null
                    : TextInterceptPatch.CleanText(header);

                string announcement = cleanedHeader != null
                    ? $"{cleanedHeader}: {cleanedValue}"
                    : cleanedValue;

                Plugin.Speech?.Speak(announcement, "Slider");
                Plugin.Logger?.LogInfo($"[Nav:slider] \"{announcement}\" (spoke)");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[SliderChange] {ex.Message}");
            }
        }
    }


    /// <summary>
    /// Enables Left/Right arrow keys to adjust slider values.
    ///
    /// The game's slider interaction was designed for controller: d-pad selects
    /// plus/minus button, trigger clicks it. In mouse mode, the user clicks the
    /// plus/minus arrows directly. Neither path works for keyboard-only.
    ///
    /// This patch targets the same three subclass update() methods as SliderChangePatch.
    /// After each update, if the slider is hovered and Left/Right arrow was pressed,
    /// we directly invoke the increment/decrement logic. The value change is picked up
    /// by the next frame's update() which refreshes the display.
    /// </summary>
    [HarmonyPatch]
    public static class SliderArrowPatch
    {
        private static FieldInfo _settingField;
        private static MethodInfo _incrementStateMethod;
        private static FieldInfo _pointerField;
        private static MethodInfo _boundPointerMethod;
        private static FieldInfo _characterField;
        private static MethodInfo _cycleActivityMethod;
        private static bool _initialized;
        private static bool _initFailed;

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> TargetMethods()
        {
            var seen = new HashSet<MethodBase>();
            foreach (var typeName in new[]
            {
                "UITextCustomSliderButton",
                "UITextSliderSettingsButton",
                "UITextSliderCampActivityButton"
            })
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) continue;
                var method = AccessTools.Method(type, "update");
                if (method != null && seen.Add(method))
                {
                    Plugin.Logger?.LogInfo($"[SliderArrow] Patching {method.DeclaringType?.Name}.update()");
                    yield return method;
                }
            }
        }

        private static void Initialize()
        {
            if (_initFailed) return;
            try
            {
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

                _initialized = true;

                if (_initialized)
                    Plugin.Logger?.LogInfo("[SliderArrow] Initialized successfully");
                else
                {
                    Plugin.Logger?.LogError("[SliderArrow] Init failed");
                    _initFailed = true;
                }
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
                if (!_initialized) { if (_initFailed) return; Initialize(); if (!_initialized) return; }

                bool left = Input.GetKeyDown(KeyCode.LeftArrow);
                bool right = Input.GetKeyDown(KeyCode.RightArrow);
                if (!left && !right) return;

                // Only act on the last-focused slider (tracked by SliderHoverPatch).
                // We don't use getHover() because popup navigation can move the cursor
                // away from the slider before this postfix runs.
                if (!ReferenceEquals(__instance, SliderHoverPatch.LastFocusedSliderButton)) return;

                int direction = right ? 1 : -1;
                string typeName = __instance.GetType().Name;

                if (typeName == "UITextSliderSettingsButton" && _settingField != null && _incrementStateMethod != null)
                {
                    object setting = _settingField.GetValue(__instance);
                    if (setting != null)
                        _incrementStateMethod.Invoke(setting, new object[] { direction });
                }
                else if (typeName == "UITextCustomSliderButton" && _pointerField != null)
                {
                    int pointer = (int)_pointerField.GetValue(__instance);
                    _pointerField.SetValue(__instance, pointer + direction);
                    _boundPointerMethod?.Invoke(__instance, null);
                }
                else if (typeName == "UITextSliderCampActivityButton" && _characterField != null && _cycleActivityMethod != null)
                {
                    object character = _characterField.GetValue(__instance);
                    if (character != null)
                        _cycleActivityMethod.Invoke(character, new object[] { direction });
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[SliderArrow] {ex.Message}");
            }
        }
    }
}

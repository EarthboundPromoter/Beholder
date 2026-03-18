using HarmonyLib;
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Announces focused element text when the NavigationCursor index changes.
    ///
    /// Replaces ButtonHoverPatch. Instead of observing hoverIndex (which depends
    /// on virtual mouse position and resets to -1 every frame), reads the
    /// NavigationCursor's authoritative index and speaks the element at that index.
    ///
    /// Also handles Enter-key activation: writes buttonPressIndexLeft at the
    /// current NavigationCursor index.
    ///
    /// Postfix on GUIControl.update() — runs after all control updates.
    /// </summary>
    [HarmonyPatch]
    public static class IndexNavigationPatch
    {
        private static FieldInfo _numericButtonsField;
        private static FieldInfo _listButtonsField;
        private static FieldInfo _menuTabField;
        private static FieldInfo _sheetComplexField;
        private static FieldInfo _horizontalMenuButtonsField;
        private static MethodInfo _getButtonsListMethod;
        private static FieldInfo _contentField;
        private static FieldInfo _buttonPressIndexLeftField;
        private static bool _initialized;
        private static bool _initFailed;

        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
        {
            var guiControlType = AccessTools.TypeByName("GUIControl");
            if (guiControlType == null)
            {
                Plugin.Logger?.LogError("[IndexNav] GUIControl type not found");
                return null;
            }
            var method = AccessTools.Method(guiControlType, "update");
            if (method == null)
            {
                Plugin.Logger?.LogError("[IndexNav] GUIControl.update() not found");
                return null;
            }
            Plugin.Logger?.LogInfo("[IndexNav] Found GUIControl.update() for patching");
            return method;
        }

        private static void Initialize()
        {
            if (_initFailed) return;
            try
            {
                _numericButtonsField = AccessTools.Field(typeof(GUIControl), "numericButtons");
                _listButtonsField = AccessTools.Field(typeof(GUIControl), "listButtons");
                _menuTabField = AccessTools.Field(typeof(GUIControl), "menuTab");
                _sheetComplexField = AccessTools.Field(typeof(GUIControl), "sheetComplex");

                var extraRowType = AccessTools.TypeByName("GUIControl+ExtraButtonRowSheetComplex");
                if (extraRowType != null)
                    _horizontalMenuButtonsField = AccessTools.Field(extraRowType, "horizontalMenuButtons");

                var baseType = AccessTools.TypeByName("UIButtonControlBase");
                if (baseType == null)
                {
                    Plugin.Logger?.LogError("[IndexNav] UIButtonControlBase not found");
                    _initFailed = true;
                    return;
                }

                _getButtonsListMethod = AccessTools.Method(baseType, "getButtonsList");
                _buttonPressIndexLeftField = AccessTools.Field(baseType, "buttonPressIndexLeft");
                _contentField = AccessTools.Field(typeof(UITextBlock), "content");

                _initialized = _numericButtonsField != null
                    && _getButtonsListMethod != null
                    && _buttonPressIndexLeftField != null
                    && _contentField != null;

                if (_initialized)
                    Plugin.Logger?.LogInfo("[IndexNav] Initialized successfully");
                else
                {
                    Plugin.Logger?.LogError("[IndexNav] Init failed — missing reflection targets");
                    _initFailed = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[IndexNav] Init exception: {ex.Message}");
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

                object numericButtons = _numericButtonsField.GetValue(__instance);
                if (numericButtons != null)
                    ProcessControl(numericButtons, isNumeric: true);

                if (_listButtonsField != null)
                {
                    object listButtons = _listButtonsField.GetValue(__instance);
                    if (listButtons != null)
                        ProcessControl(listButtons, isNumeric: false);
                }

                if (_menuTabField != null)
                {
                    object menuTab = _menuTabField.GetValue(__instance);
                    if (menuTab != null)
                        ProcessControl(menuTab, isNumeric: false);
                }

                // UIHorizontalMenuButtons — lives inside ExtraButtonRowSheetComplex,
                // used on difficulty selector and potentially other screens.
                if (_sheetComplexField != null && _horizontalMenuButtonsField != null)
                {
                    object sheetComplex = _sheetComplexField.GetValue(__instance);
                    if (sheetComplex != null && _horizontalMenuButtonsField.DeclaringType.IsInstanceOfType(sheetComplex))
                    {
                        object horizontalButtons = _horizontalMenuButtonsField.GetValue(sheetComplex);
                        if (horizontalButtons != null)
                            ProcessControl(horizontalButtons, isNumeric: false);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[IndexNav] {ex.Message}");
            }
        }

        private static void ProcessControl(object control, bool isNumeric)
        {
            var cursorState = NavigationCursor.GetState(control);
            if (cursorState == null) return;

            int index = cursorState.Index;
            if (index < 0) return; // not yet navigated to

            // Enter key activation is handled by SkaldIOPatch mapping Enter → left mouse click.
            // The cursor is already positioned on the element at our index.

            // Speak if index changed since last announcement
            string text = ReadButtonText(control, index);
            if (text == null)
            {
                Plugin.Logger?.LogDebug($"[Nav:diag:{control.GetType().Name}] idx={index} text=NULL");
                return;
            }

            // Numeric buttons include their key shortcut (1-9) for orientation
            if (isNumeric)
                text = $"{index + 1}: {text}";

            if (text == cursorState.LastSpoken)
            {
                return; // same text, no need to log every frame
            }
            cursorState.LastSpoken = text;
            Plugin.Speech?.Speak(text, "Nav");
            Plugin.Logger?.LogInfo($"[Nav:idx:{control.GetType().Name}] {index} \"{text}\"");
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

            string cleaned = TextInterceptPatch.CleanText(raw);
            if (string.IsNullOrWhiteSpace(cleaned)) return null;
            if (cleaned == "..." || cleaned == "\u2026") return "dot dot dot";
            return cleaned;
        }
    }
}

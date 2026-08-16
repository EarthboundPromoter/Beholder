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
    /// NavigationCursor index (a mod-side mirror of the game's
    /// currentSelectedButton — build-plan WP4 replaces both this mirror and this
    /// per-frame postfix with a note-only hook on UICanvas.setCurrentSelectedButton
    /// plus end-of-frame composition) and speaks the element at that index.
    /// Enter-key activation is SkaldIOPatch's job (Enter maps to a click).
    ///
    /// Postfix on GUIControl.update() — runs after all control updates.
    /// </summary>
    [HarmonyPatch]
    public static class IndexNavigationPatch
    {
        private static FieldInfo _numericButtonsField;
        private static FieldInfo _listButtonsField;
        private static FieldInfo _menuTabField;
        private static MethodInfo _getButtonsListMethod;
        private static FieldInfo _contentField;

        // Primary navigable control — whatever getControllerScrollableList() returns
        private static MethodInfo _getControllerScrollableListMethod;

        // Feat tree: read feat name from Node objects when button text is unavailable
        private static FieldInfo _featField;
        private static MethodInfo _getNameMethod;
        private static MethodInfo _getScrollableElementsMethod;

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
                _getControllerScrollableListMethod = AccessTools.Method(typeof(GUIControl), "getControllerScrollableList");

                var baseType = AccessTools.TypeByName("UIButtonControlBase");
                if (baseType == null)
                {
                    Plugin.Logger?.LogError("[IndexNav] UIButtonControlBase not found");
                    _initFailed = true;
                    return;
                }

                _getButtonsListMethod = AccessTools.Method(baseType, "getButtonsList");
                _contentField = AccessTools.Field(typeof(UITextBlock), "content");

                // Feat tree node → feat name (fallback when button text is unavailable)
                var uiCanvasType = AccessTools.TypeByName("UICanvas");
                if (uiCanvasType != null)
                    _getScrollableElementsMethod = AccessTools.Method(uiCanvasType, "getScrollableElements");
                var nodeType = AccessTools.TypeByName("UIFeatTree+FeatTreeCollection+Node");
                if (nodeType != null)
                    _featField = AccessTools.Field(nodeType, "feat");
                var featType = AccessTools.TypeByName("FeatContainer+Feat");
                if (featType != null)
                    _getNameMethod = AccessTools.Method(featType, "getName");

                _initialized = _numericButtonsField != null
                    && _getButtonsListMethod != null
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

                // Process whatever getControllerScrollableList() returns — the game's
                // primary navigable control for this screen. Covers UIFeatTree,
                // UIHorizontalMenuButtons, and any other control the game navigates.
                // Dedup in ProcessControl prevents double-speech if this returns
                // a control already processed above (numericButtons, listButtons, etc).
                if (_getControllerScrollableListMethod != null)
                {
                    try
                    {
                        object primaryControl = _getControllerScrollableListMethod.Invoke(__instance, null);
                        if (primaryControl != null
                            && primaryControl != numericButtons
                            && (_listButtonsField == null || primaryControl != _listButtonsField.GetValue(__instance))
                            && (_menuTabField == null || primaryControl != _menuTabField.GetValue(__instance)))
                        {
                            ProcessControl(primaryControl, isNumeric: false);
                        }
                    }
                    catch
                    {
                        // Non-conforming control on this screen — graceful silence.
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

            // Fallback: for image-based controls (feat tree nodes, etc.),
            // read from the element at the current index via reflection
            if (text == null)
                text = ReadFeatNodeName(control, index);

            if (text == null) return;

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
            IList buttons;
            try { buttons = _getButtonsListMethod.Invoke(control, null) as IList; }
            catch { return null; } // control is not a UIButtonControlBase (e.g., UIFeatTree)
            if (buttons == null || index < 0 || index >= buttons.Count) return null;
            object button = buttons[index];
            if (button == null) return null;

            string raw = null;
            try { raw = _contentField.GetValue(button) as string; }
            catch { return null; }

            if (string.IsNullOrWhiteSpace(raw) || raw == " ") return null;

            string cleaned = TextCleaner.CleanText(raw);
            if (string.IsNullOrWhiteSpace(cleaned)) return null;
            if (cleaned == "..." || cleaned == "\u2026") return "dot dot dot";
            return cleaned;
        }

        /// <summary>
        /// Fallback for image-based controls: read feat name from UIFeatTree nodes.
        /// Gets the scrollable element at the given index, reads its private 'feat'
        /// field, and calls getName() on the feat object.
        /// </summary>
        private static string ReadFeatNodeName(object control, int index)
        {
            if (_featField == null || _getNameMethod == null || _getScrollableElementsMethod == null)
                return null;

            try
            {
                var elements = _getScrollableElementsMethod.Invoke(control, null)
                    as System.Collections.Generic.List<UIElement>;
                if (elements == null) return null;
                if (index < 0 || index >= elements.Count) return null;

                object node = elements[index];
                if (node == null) return null;
                if (!_featField.DeclaringType.IsInstanceOfType(node)) return null;

                object feat = _featField.GetValue(node);
                if (feat == null) return null;

                string name = _getNameMethod.Invoke(feat, null) as string;
                if (string.IsNullOrWhiteSpace(name)) return null;
                return TextCleaner.CleanText(name);
            }
            catch
            {
                return null;
            }
        }
    }
}

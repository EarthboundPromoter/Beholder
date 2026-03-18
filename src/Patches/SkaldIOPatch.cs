using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Enables keyboard navigation in SKALD's menu system.
    ///
    /// Two parts:
    ///
    /// 1. Arrow key input: Patches getOptionSelectionButton{Down,Up,Right,Left}
    ///    to also return true on arrow key press. These methods are called by
    ///    BaseMenuState, CharacterBuilderBaseState, InfoBaseState, SceneBaseState,
    ///    and PopUpBase to drive navigation — no controller gate on the callers.
    ///
    /// 2. Controller gate removal: The game gates setMouseToClosestOption* and
    ///    setMouseToUIElement behind isControllerConnected(). Rather than lying
    ///    to the entire game by forcing that true (which changes combat behavior,
    ///    inventory interaction, button labels, and renders controller glyphs),
    ///    we patch only the 4 navigation methods to remove their controller check.
    ///
    /// NOTE: Patches are NOT applied via PatchAll(). SkaldIO has a static
    /// constructor that requires game data (C64Color) to be loaded first.
    /// ApplyPatches() is called from Plugin.Update() after game state is detected.
    /// </summary>
    public static class SkaldIOPatches
    {
        private static bool _applied;

        // Cached reflection for navigation gate removal
        private static MethodInfo _getControllerScrollableList;
        private static MethodInfo _setMouseToSelectedOption;
        private static FieldInfo _sheetComplexField;
        private static MethodInfo _scrollLeftBarDown;
        private static MethodInfo _scrollLeftBarUp;
        private static MethodInfo _getCurrentControllerSelectedElement;
        private static MethodInfo _getPosition;
        private static MethodInfo _setVirtualMousePosition;
        private static PropertyInfo _posX;
        private static PropertyInfo _posY;
        private static MethodInfo _setCurrentSelectedButton;
        private static MethodInfo _getCurrentSelectedButtonIndex;
        private static MethodInfo _boundCurrentSelectedButtons;
        private static MethodInfo _incrementCurrentSelectedButton;
        private static MethodInfo _decrementCurrentSelectedButton;
        private static MethodInfo _getButtonsList;
        private static MethodInfo _canControllerScrollDown;
        private static MethodInfo _canControllerScrollUp;

        // SheetComplexSettings fallback
        private static MethodInfo _getListButtonsMethod;

        // PopUpUIBase reflection
        private static MethodInfo _popupGetScrollableCanvas;
        private static bool _navInitialized;
        private static bool _navInitFailed;

        public static void ApplyPatches(Harmony harmony)
        {
            if (_applied) return;
            _applied = true;

            try
            {
                var skaldIOType = AccessTools.TypeByName("SkaldIO");
                if (skaldIOType == null)
                {
                    Plugin.Logger?.LogError("[SkaldIO] SkaldIO type not found — arrow nav unavailable");
                    return;
                }

                // Arrow key input patches
                var postfixDown  = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_DownArrow));
                var postfixUp    = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_UpArrow));
                var postfixRight = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_RightArrow));
                var postfixLeft  = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_LeftArrow));

                PatchMethod(harmony, skaldIOType, "getOptionSelectionButtonDown",  postfixDown);
                PatchMethod(harmony, skaldIOType, "getOptionSelectionButtonUp",    postfixUp);
                PatchMethod(harmony, skaldIOType, "getOptionSelectionButtonRight", postfixRight);
                PatchMethod(harmony, skaldIOType, "getOptionSelectionButtonLeft",  postfixLeft);

                // Enter → left-click, Right Shift → right-click.
                // Separate postfixes for getMouseUp (release) and getMousePressed (press).
                var postfixMouseUp = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_MouseUp));
                var postfixMousePressed = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_MousePressed));
                PatchMethod(harmony, skaldIOType, "getMouseUp", postfixMouseUp);
                PatchMethod(harmony, skaldIOType, "getMousePressed", postfixMousePressed);

                // Suppress arrow key → scrollbar conflict. getButtonScrollUp/Down
                // respond to Up/Down arrow held, which conflicts with our navigation.
                // Mouse wheel and click-based scrolling are unaffected.
                var prefixFalse = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Prefix_ReturnFalse));
                var scrollUp = AccessTools.Method(skaldIOType, "getButtonScrollUp");
                var scrollDown = AccessTools.Method(skaldIOType, "getButtonScrollDown");
                if (scrollUp != null) { harmony.Patch(scrollUp, prefix: prefixFalse); Plugin.Logger?.LogInfo("[SkaldIO] Patched getButtonScrollUp"); }
                if (scrollDown != null) { harmony.Patch(scrollDown, prefix: prefixFalse); Plugin.Logger?.LogInfo("[SkaldIO] Patched getButtonScrollDown"); }

                // Fix hover-search in increment/decrementCurrentSelectedButton on UICanvas.
                // This is the ROOT FIX — replaces hover-based navigation with index math.
                // All callers (GUIControl, PopUpUIBase, zone-switching) benefit automatically.
                var uiCanvasType = AccessTools.TypeByName("UICanvas");
                if (uiCanvasType != null)
                {
                    var incMethod = AccessTools.Method(uiCanvasType, "incrementCurrentSelectedButton");
                    var decMethod = AccessTools.Method(uiCanvasType, "decrementCurrentSelectedButton");
                    var prefixInc = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Prefix_IncrementSelectedButton));
                    var prefixDec = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Prefix_DecrementSelectedButton));
                    if (incMethod != null) { harmony.Patch(incMethod, prefix: prefixInc); Plugin.Logger?.LogInfo("[SkaldIO] Patched UICanvas.incrementCurrentSelectedButton"); }
                    if (decMethod != null) { harmony.Patch(decMethod, prefix: prefixDec); Plugin.Logger?.LogInfo("[SkaldIO] Patched UICanvas.decrementCurrentSelectedButton"); }
                }

                // Controller gate removal — GUIControl navigation and cursor positioning
                var guiControlType = AccessTools.TypeByName("GUIControl");
                if (guiControlType != null && uiCanvasType != null)
                {
                    var prefixAbove = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Prefix_SetMouseAbove));
                    var prefixBelow = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Prefix_SetMouseBelow));
                    var prefixSetMouse = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Prefix_SetMouseToUIElement));

                    var aboveMethod = AccessTools.Method(guiControlType, "setMouseToClosestOptionAbove");
                    var belowMethod = AccessTools.Method(guiControlType, "setMouseToClosestOptionBelow");
                    var setMouseMethod = AccessTools.Method(guiControlType, "setMouseToUIElement",
                        new[] { uiCanvasType, typeof(int), typeof(int) });

                    if (aboveMethod != null) { harmony.Patch(aboveMethod, prefix: prefixAbove); Plugin.Logger?.LogInfo("[SkaldIO] Patched setMouseToClosestOptionAbove"); }
                    if (belowMethod != null) { harmony.Patch(belowMethod, prefix: prefixBelow); Plugin.Logger?.LogInfo("[SkaldIO] Patched setMouseToClosestOptionBelow"); }
                    if (setMouseMethod != null) { harmony.Patch(setMouseMethod, prefix: prefixSetMouse); Plugin.Logger?.LogInfo("[SkaldIO] Patched setMouseToUIElement"); }
                }

                // SheetComplexSettings.getControllerScrollableList — falls back to
                // listButtons when sliderControl is null (fixes keybindings screen)
                var sheetComplexSettingsType = AccessTools.TypeByName("GUIControl+SheetComplexSettings");
                if (sheetComplexSettingsType != null)
                {
                    var getListMethod = AccessTools.Method(sheetComplexSettingsType, "getControllerScrollableList");
                    if (getListMethod != null)
                    {
                        var postfixFallback = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_SettingsScrollableList));
                        harmony.Patch(getListMethod, postfix: postfixFallback);
                        Plugin.Logger?.LogInfo("[SkaldIO] Patched SheetComplexSettings.getControllerScrollableList");
                    }
                }

                // PopUpUIBase.setMouseToSelectedButton — controller gate removal
                var popupUIBaseType = AccessTools.TypeByName("PopUpBase+PopUpUIBase");
                if (popupUIBaseType != null)
                {
                    var popupSetMouse = AccessTools.Method(popupUIBaseType, "setMouseToSelectedButton");
                    if (popupSetMouse != null)
                    {
                        var prefixPopup = new HarmonyMethod(typeof(SkaldIOPatches), nameof(Prefix_PopupSetMouseToSelected));
                        harmony.Patch(popupSetMouse, prefix: prefixPopup);
                        Plugin.Logger?.LogInfo("[SkaldIO] Patched PopUpUIBase.setMouseToSelectedButton");
                    }
                }
                else
                {
                    Plugin.Logger?.LogWarning("[SkaldIO] PopUpBase+PopUpUIBase not found");
                }

                Plugin.Logger?.LogInfo("[SkaldIO] Arrow-key navigation patches applied (no controller spoofing)");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[SkaldIO] Failed to apply patches: {ex.Message}");
            }
        }

        private static void PatchMethod(Harmony harmony, Type type, string methodName, HarmonyMethod postfix)
        {
            var method = AccessTools.Method(type, methodName);
            if (method == null)
            {
                Plugin.Logger?.LogWarning($"[SkaldIO] Method not found: {methodName}");
                return;
            }
            harmony.Patch(method, postfix: postfix);
            Plugin.Logger?.LogInfo($"[SkaldIO] Patched {methodName}");
        }

        private static void InitializeNavReflection()
        {
            if (_navInitFailed) return;
            try
            {
                var guiControlType = typeof(GUIControl);
                var uiCanvasType = AccessTools.TypeByName("UICanvas");
                var uiElementType = AccessTools.TypeByName("UIElement");
                var sheetComplexType = AccessTools.TypeByName("SheetComplex");
                var skaldPoint2DType = AccessTools.TypeByName("SkaldPoint2D");
                var skaldIOType = AccessTools.TypeByName("SkaldIO");

                _getControllerScrollableList = AccessTools.Method(guiControlType, "getControllerScrollableList");
                _sheetComplexField = AccessTools.Field(guiControlType, "sheetComplex");
                _setMouseToSelectedOption = AccessTools.Method(guiControlType, "setMouseToSelectedOption");

                if (uiCanvasType != null)
                {
                    _canControllerScrollDown = AccessTools.Method(uiCanvasType, "canControllerScrollDown");
                    _canControllerScrollUp = AccessTools.Method(uiCanvasType, "canControllerScrollUp");
                    _setCurrentSelectedButton = AccessTools.Method(uiCanvasType, "setCurrentSelectedButton", new[] { typeof(int) });
                    _getCurrentSelectedButtonIndex = AccessTools.Method(uiCanvasType, "getCurrentSelectedButtonIndex");
                    _boundCurrentSelectedButtons = AccessTools.Method(uiCanvasType, "boundCurrentSelectedButtons");
                    _incrementCurrentSelectedButton = AccessTools.Method(uiCanvasType, "incrementCurrentSelectedButton");
                    _decrementCurrentSelectedButton = AccessTools.Method(uiCanvasType, "decrementCurrentSelectedButton");
                    _getCurrentControllerSelectedElement = AccessTools.Method(uiCanvasType, "getCurrentControllerSelectedElement");
                }

                var buttonBaseType = AccessTools.TypeByName("UIButtonControlBase");
                if (buttonBaseType != null)
                {
                    _getButtonsList = AccessTools.Method(buttonBaseType, "getButtonsList");
                }

                if (uiElementType != null)
                {
                    _getPosition = AccessTools.Method(uiElementType, "getPosition");
                }

                if (skaldPoint2DType != null)
                {
                    _posX = AccessTools.Property(skaldPoint2DType, "X");
                    _posY = AccessTools.Property(skaldPoint2DType, "Y");
                }

                if (sheetComplexType != null)
                {
                    _scrollLeftBarDown = AccessTools.Method(sheetComplexType, "scrollLeftBarDown");
                    _scrollLeftBarUp = AccessTools.Method(sheetComplexType, "scrollLeftBarUp");
                }

                if (skaldIOType != null)
                {
                    _setVirtualMousePosition = AccessTools.Method(skaldIOType, "setVirtualMousePosition",
                        new[] { typeof(int), typeof(int) });
                }

                // SheetComplexSettings listButtons fallback
                var settingsSheetType = AccessTools.TypeByName("GUIControl+SheetComplexSettings");
                if (settingsSheetType != null)
                    _getListButtonsMethod = AccessTools.Method(settingsSheetType, "getListButtons");

                // PopUpUIBase
                var popupUIBaseType = AccessTools.TypeByName("PopUpBase+PopUpUIBase");
                if (popupUIBaseType != null)
                {
                    _popupGetScrollableCanvas = AccessTools.Method(popupUIBaseType, "getControllerScrollableUICanvas");
                }

                _navInitialized = _getControllerScrollableList != null
                    && _canControllerScrollDown != null
                    && _canControllerScrollUp != null
                    && _setCurrentSelectedButton != null
                    && _getCurrentSelectedButtonIndex != null
                    && _boundCurrentSelectedButtons != null
                    && _incrementCurrentSelectedButton != null
                    && _decrementCurrentSelectedButton != null
                    && _setMouseToSelectedOption != null
                    && _getCurrentControllerSelectedElement != null
                    && _getPosition != null
                    && _setVirtualMousePosition != null
                    && _posX != null && _posY != null;

                if (_navInitialized)
                    Plugin.Logger?.LogInfo("[SkaldIO] Navigation reflection initialized");
                else
                {
                    Plugin.Logger?.LogError("[SkaldIO] Navigation reflection init failed — missing targets");
                    _navInitFailed = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[SkaldIO] Nav reflection init: {ex.Message}");
                _navInitFailed = true;
            }
        }

        // --- Arrow key postfixes (unchanged) ---

        /// <summary>
        /// getMouseUp postfix: Enter key-up → left click release, Right Shift key-down → right click release.
        /// </summary>
        static void Postfix_MouseUp(int __0, ref bool __result)
        {
            if (__0 == 0 && Input.GetKeyUp(KeyCode.Return)) __result = true;
            if (__0 == 1 && Input.GetKeyUp(KeyCode.RightShift)) __result = true;
        }

        /// <summary>
        /// getMousePressed postfix: Enter key-down → left click press, Right Shift key-down → right click press.
        /// </summary>
        static void Postfix_MousePressed(int __0, ref bool __result)
        {
            if (__0 == 0 && Input.GetKeyDown(KeyCode.Return)) __result = true;
            if (__0 == 1 && Input.GetKeyDown(KeyCode.RightShift)) __result = true;
        }
        static void Postfix_DownArrow(ref bool __result)   { if (Input.GetKeyDown(KeyCode.DownArrow))  __result = true; }
        static void Postfix_UpArrow(ref bool __result)     { if (Input.GetKeyDown(KeyCode.UpArrow))    __result = true; }
        static void Postfix_RightArrow(ref bool __result)  { if (Input.GetKeyDown(KeyCode.RightArrow)) __result = true; }
        static void Postfix_LeftArrow(ref bool __result)   { if (Input.GetKeyDown(KeyCode.LeftArrow))  __result = true; }

        // --- Scrollbar arrow key suppression ---

        static bool Prefix_ReturnFalse(ref bool __result)
        {
            __result = false;
            return false; // skip original
        }

        // --- Settings scrollable list fallback ---

        /// <summary>
        /// When SheetComplexSettings.getControllerScrollableList() returns null
        /// (sliderControl not set, e.g., keybindings screen), fall back to listButtons.
        /// </summary>
        static void Postfix_SettingsScrollableList(object __instance, ref object __result)
        {
            if (__result != null) return;
            if (_getListButtonsMethod != null)
            {
                try { __result = _getListButtonsMethod.Invoke(__instance, null); }
                catch { }
            }
        }

        // --- Root fix: index-based increment/decrement on UICanvas ---

        /// <summary>
        /// Replaces UICanvas.incrementCurrentSelectedButton().
        /// Uses currentSelectedButton + 1 instead of searching by hover position.
        /// This single fix makes all callers (GUIControl, PopUpUIBase, zone-switching) work.
        /// </summary>
        static bool Prefix_IncrementSelectedButton(object __instance)
        {
            if (!_navInitialized) { if (_navInitFailed) return true; InitializeNavReflection(); if (!_navInitialized) return true; }
            try
            {
                int current = (int)_getCurrentSelectedButtonIndex.Invoke(__instance, null);
                _setCurrentSelectedButton.Invoke(__instance, new object[] { current + 1 });
                _boundCurrentSelectedButtons.Invoke(__instance, null);
                int newIndex = (int)_getCurrentSelectedButtonIndex.Invoke(__instance, null);
                NavigationCursor.SetIndex(__instance, newIndex);
                Plugin.Logger?.LogInfo($"[SkaldIO:inc] {__instance.GetType().Name} {current}→{newIndex}");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[SkaldIO] Increment: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Replaces UICanvas.decrementCurrentSelectedButton().
        /// Uses currentSelectedButton - 1 instead of searching by hover position.
        /// </summary>
        static bool Prefix_DecrementSelectedButton(object __instance)
        {
            if (!_navInitialized) { if (_navInitFailed) return true; InitializeNavReflection(); if (!_navInitialized) return true; }
            try
            {
                int current = (int)_getCurrentSelectedButtonIndex.Invoke(__instance, null);
                _setCurrentSelectedButton.Invoke(__instance, new object[] { current - 1 });
                _boundCurrentSelectedButtons.Invoke(__instance, null);
                int newIndex = (int)_getCurrentSelectedButtonIndex.Invoke(__instance, null);
                NavigationCursor.SetIndex(__instance, newIndex);
                Plugin.Logger?.LogInfo($"[SkaldIO:dec] {__instance.GetType().Name} {current}→{newIndex}");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[SkaldIO] Decrement: {ex.Message}");
            }
            return false;
        }

        // --- Controller gate removal prefixes ---

        /// <summary>
        /// Replaces GUIControl.setMouseToClosestOptionAbove() — removes isControllerConnected gate.
        /// The original logic (call getControllerScrollableList().decrementCurrentSelectedButton()
        /// then setMouseToSelectedOption()) now works correctly because we fixed decrement above.
        /// </summary>
        static bool Prefix_SetMouseAbove(object __instance)
        {
            if (!_navInitialized) { if (_navInitFailed) return true; InitializeNavReflection(); if (!_navInitialized) return true; }
            try
            {
                object list = _getControllerScrollableList.Invoke(__instance, null);
                if (list == null) return false;

                if ((bool)_canControllerScrollDown.Invoke(list, null))
                {
                    // decrementCurrentSelectedButton is now index-based (our prefix)
                    _decrementCurrentSelectedButton.Invoke(list, null);
                }
                else
                {
                    object sheetComplex = _sheetComplexField.GetValue(__instance);
                    if (sheetComplex != null && _scrollLeftBarDown != null)
                        _scrollLeftBarDown.Invoke(sheetComplex, null);
                }
                _setMouseToSelectedOption.Invoke(__instance, null);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[SkaldIO] SetMouseAbove: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Replaces GUIControl.setMouseToClosestOptionBelow() — removes isControllerConnected gate.
        /// </summary>
        static bool Prefix_SetMouseBelow(object __instance)
        {
            if (!_navInitialized) { if (_navInitFailed) return true; InitializeNavReflection(); if (!_navInitialized) return true; }
            try
            {
                object list = _getControllerScrollableList.Invoke(__instance, null);
                if (list == null) return false;

                if ((bool)_canControllerScrollUp.Invoke(list, null))
                {
                    // incrementCurrentSelectedButton is now index-based (our prefix)
                    _incrementCurrentSelectedButton.Invoke(list, null);
                }
                else
                {
                    object sheetComplex = _sheetComplexField.GetValue(__instance);
                    if (sheetComplex != null && _scrollLeftBarUp != null)
                        _scrollLeftBarUp.Invoke(sheetComplex, null);
                }
                _setMouseToSelectedOption.Invoke(__instance, null);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[SkaldIO] SetMouseBelow: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Replaces GUIControl.setMouseToUIElement(UICanvas, int, int) without the isControllerConnected gate.
        /// </summary>
        static bool Prefix_SetMouseToUIElement(object __instance, object __0, int __1, int __2)
        {
            if (!_navInitialized) { if (_navInitFailed) return true; InitializeNavReflection(); if (!_navInitialized) return true; }
            try
            {
                if (__0 == null) return false;

                object element = _getCurrentControllerSelectedElement.Invoke(__0, null);
                if (element == null) return false;

                object position = _getPosition.Invoke(element, null);
                int x = (int)_posX.GetValue(position, null);
                int y = (int)_posY.GetValue(position, null);
                _setVirtualMousePosition.Invoke(null, new object[] { x + __1, y + __2 });
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[SkaldIO] SetMouseToUIElement: {ex.Message}");
            }
            return false; // skip original
        }

        /// <summary>
        /// Replaces PopUpUIBase.setMouseToSelectedButton() without the isControllerConnected gate.
        /// </summary>
        static bool Prefix_PopupSetMouseToSelected(object __instance)
        {
            if (!_navInitialized) { if (_navInitFailed) return true; InitializeNavReflection(); if (!_navInitialized) return true; }
            try
            {
                if (_popupGetScrollableCanvas == null) return true;
                object canvas = _popupGetScrollableCanvas.Invoke(__instance, null);
                if (canvas == null) return false;

                object element = _getCurrentControllerSelectedElement.Invoke(canvas, null);
                if (element == null) return false;

                object position = _getPosition.Invoke(element, null);
                int x = (int)_posX.GetValue(position, null);
                int y = (int)_posY.GetValue(position, null);
                _setVirtualMousePosition.Invoke(null, new object[] { x + 8, y - 8 });
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[SkaldIO] PopupSetMouse: {ex.Message}");
            }
            return false; // skip original
        }
    }
}

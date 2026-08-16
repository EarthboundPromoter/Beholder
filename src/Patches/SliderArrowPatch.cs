using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Slider voicing on the NATIVE controller idiom (owner ruling 2026-08-16:
    /// follow the game's expectations — the WP6 arrow-key adjust layer is
    /// deleted, freeing the arrows for the future review layer).
    ///
    /// The game's slider mechanic under controller mode: each row's focusable
    /// element is its currently-chosen minus/plus arrow button; stick-sideways
    /// flips which arrow is chosen (UITextSliderButton.controllerScrollSideways*,
    /// applied to every row in the control at once — native design); LT clicks
    /// the arrow and steps the value (each subclass's update consumes
    /// getLeftUp and re-renders the value block in the same call).
    ///
    /// Two joins, both mechanism-class, both note-only:
    ///  - the sideways flip → note → drain speaks the arrow now under the
    ///    cursor ("Plus." / "Minus."), read from the game's own
    ///    controllerSelectPlusButton at drain time;
    ///  - a click landing while a slider row is hovered → note → drain speaks
    ///    the row's rendered value. The mutation and the value re-render happen
    ///    inside the same row update BEFORE this control-level postfix runs, so
    ///    the drain reads a fresh value the same frame (the arrow-key path's
    ///    one-frame-stale bug, ledger B3, died with that path).
    ///
    /// Also hosts the slider-row describer used by the Pump's selection
    /// composition ("Header: Value, plus/minus" + queued description).
    /// </summary>
    [HarmonyPatch]
    public static class SliderArrowPatch
    {
        private static FieldInfo _hoverButtonField;
        private static FieldInfo _headerTextBlockField;
        private static FieldInfo _currentValueTextBlockField;
        private static FieldInfo _contentField;
        private static FieldInfo _selectPlusField;   // UITextSliderButton.controllerSelectPlusButton
        private static FieldInfo _minusButtonField;  // UITextSliderButton.minusButton
        private static FieldInfo _plusButtonField;   // UITextSliderButton.plusButton
        private static FieldInfo _settingField;      // UITextSliderSettingsButton.setting
        private static MethodInfo _getFullDescriptionMethod; // SkaldBaseObject.getFullDescription
        private static MethodInfo _getElementsMethod; // UICanvas.getElements
        private static MethodInfo _getMouseUpMethod; // SkaldIO.getMouseUp(int)
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
                var buttonType = AccessTools.TypeByName("UITextSliderControl+UITextSliderButton");
                var skaldIOType = AccessTools.TypeByName("SkaldIO");
                if (controlType == null || buttonType == null || skaldIOType == null) { _initFailed = true; return; }

                _hoverButtonField = AccessTools.Field(controlType, "hoverButton");
                _headerTextBlockField = AccessTools.Field(buttonType, "headerTextBlock");
                _currentValueTextBlockField = AccessTools.Field(buttonType, "currentValueTextBlock");
                _selectPlusField = AccessTools.Field(buttonType, "controllerSelectPlusButton");
                _minusButtonField = AccessTools.Field(buttonType, "minusButton");
                _plusButtonField = AccessTools.Field(buttonType, "plusButton");
                _getElementsMethod = AccessTools.Method(AccessTools.TypeByName("UICanvas"), "getElements");
                _contentField = AccessTools.Field(typeof(UITextBlock), "content");
                _getMouseUpMethod = AccessTools.Method(skaldIOType, "getMouseUp", new[] { typeof(int) });

                var settingsButtonType = AccessTools.TypeByName("UITextSliderControl+UITextSliderSettingsButton");
                if (settingsButtonType != null)
                    _settingField = AccessTools.Field(settingsButtonType, "setting");
                var baseObjectType = AccessTools.TypeByName("SkaldBaseObject");
                if (baseObjectType != null)
                    _getFullDescriptionMethod = AccessTools.Method(baseObjectType, "getFullDescription");

                _initialized = _hoverButtonField != null && _headerTextBlockField != null
                    && _currentValueTextBlockField != null && _contentField != null
                    && _getMouseUpMethod != null;
                if (!_initialized) { Plugin.Logger?.LogError("[SliderArrow] Init failed — missing fields"); _initFailed = true; }
                if (_selectPlusField == null)
                    Plugin.Logger?.LogWarning("[SliderArrow] controllerSelectPlusButton not found — arrow position unvoiced");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[SliderArrow] Init: {ex.Message}");
                _initFailed = true;
            }
        }

        /// <summary>Click join: a left-click release landing this frame while a
        /// slider row is hovered (the game's own hoverButton, virtual mouse
        /// included) means that row's arrow consumed it — note the row so the
        /// drain speaks the freshly rendered value. Covers every slider type and
        /// every click source (LT / Z / real mouse) through one seam.</summary>
        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            try
            {
                if (!_initialized) { if (_initFailed) return; Initialize(); if (!_initialized) return; }

                if (!(bool)_getMouseUpMethod.Invoke(null, new object[] { 0 })) return;
                object hoverButton = _hoverButtonField.GetValue(__instance);
                if (hoverButton == null) return;
                Pump.NoteSliderValue(hoverButton);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[SliderArrow] {ex.Message}");
            }
        }

        // ---------- Arrow-flip join ----------

        /// <summary>The stick-sideways flip changes no selection index, so the
        /// selection join never hears it — this is its own join on the button
        /// class's flip methods. The control applies the flip to every row, so
        /// the postfix fires once per row; latest-wins collapses it at drain.</summary>
        [HarmonyPatch]
        public static class ArrowFlipJoin
        {
            [HarmonyTargetMethods]
            static IEnumerable<MethodBase> TargetMethods()
            {
                var buttonType = AccessTools.TypeByName("UITextSliderControl+UITextSliderButton");
                if (buttonType == null)
                {
                    Plugin.Logger?.LogWarning("[SliderArrow] UITextSliderButton not found — arrow flips unvoiced");
                    yield break;
                }
                var left = AccessTools.Method(buttonType, "controllerScrollSidewaysLeft");
                var right = AccessTools.Method(buttonType, "controllerScrollSidewaysRight");
                if (left != null) yield return left;
                if (right != null) yield return right;
                Plugin.Logger?.LogInfo("[SliderArrow] Patching UITextSliderButton.controllerScrollSideways*");
            }

            [HarmonyPostfix]
            static void Postfix(object __instance)
            {
                Pump.NoteSliderArrowFlip(__instance);
            }
        }

        /// <summary>Map a slider control's scrollable element back to its owning
        /// row. UITextSliderControl.getScrollableElements returns each row's
        /// currently-chosen minus/plus ARROW button — the row itself never
        /// appears in the list — so selection composition needs this reverse
        /// lookup (visual-style modal + settings sliders; ledger B2 gap).
        /// Returns the element itself if it already is a row; null when the
        /// element belongs to no row.</summary>
        public static object RowForScrollableElement(object control, object element)
        {
            try
            {
                if (!_initialized) { if (_initFailed) return null; Initialize(); if (!_initialized) return null; }
                if (element == null) return null;
                if (_headerTextBlockField.DeclaringType.IsInstanceOfType(element)) return element;
                if (_minusButtonField == null || _plusButtonField == null || _getElementsMethod == null) return null;

                var elements = _getElementsMethod.Invoke(control, null) as System.Collections.IEnumerable;
                if (elements == null) return null;
                foreach (var e in elements)
                {
                    if (e == null || !_headerTextBlockField.DeclaringType.IsInstanceOfType(e)) continue;
                    if (ReferenceEquals(_minusButtonField.GetValue(e), element)
                        || ReferenceEquals(_plusButtonField.GetValue(e), element))
                        return e;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Which arrow the cursor is on for a row, read from the game's
        /// own flag at time of use. Null when unknowable.</summary>
        public static string ReadArrowSide(object button)
        {
            try
            {
                if (!_initialized) { if (_initFailed) return null; Initialize(); if (!_initialized) return null; }
                if (_selectPlusField == null) return null;
                if (!_selectPlusField.DeclaringType.IsInstanceOfType(button)) return null;
                return (bool)_selectPlusField.GetValue(button) ? "plus" : "minus";
            }
            catch { return null; }
        }

        // ---------- Describer (used by Pump composition and value drain) ----------

        /// <summary>"Header: Value, plus/minus" for a slider row (or just the
        /// value). Returns null if the object is not a slider button — callers
        /// fall through.</summary>
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

                string row = (cleanedHeader != null && cleanedValue != null)
                    ? $"{cleanedHeader}: {cleanedValue}"
                    : cleanedHeader ?? cleanedValue;
                if (row == null) return null;
                string side = ReadArrowSide(button);
                return side != null ? $"{row}, {side}" : row;
            }
            catch { return null; }
        }

        /// <summary>Queue the row's description behind the name announcement.
        /// Settings rows reach past the row's getDescription (which returns the
        /// setting's header + body — the header repeats the name the row
        /// announcement just spoke) to the setting's getFullDescription: the
        /// same game text, minus the duplicate name (owner verbosity ruling,
        /// 2026-08-16). Other row types keep their own getDescription.</summary>
        public static void QueueDescription(object button)
        {
            try
            {
                string desc = null;
                if (_settingField != null && _getFullDescriptionMethod != null
                    && _settingField.DeclaringType.IsInstanceOfType(button))
                {
                    object setting = _settingField.GetValue(button);
                    if (setting != null)
                        desc = _getFullDescriptionMethod.Invoke(setting, null) as string;
                }
                if (desc == null)
                {
                    var type = button.GetType();
                    if (!_descMethods.TryGetValue(type, out var method))
                    {
                        method = AccessTools.Method(type, "getDescription");
                        _descMethods[type] = method;
                    }
                    if (method == null) return;
                    desc = method.Invoke(button, null) as string;
                }
                string cleaned = string.IsNullOrWhiteSpace(desc) ? null : TextCleaner.CleanText(desc);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    Scaffold.SpeechService.SayQueued(cleaned, "SliderDesc");
            }
            catch { }
        }
    }
}

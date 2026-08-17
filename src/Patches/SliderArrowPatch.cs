using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Slider voicing on the NATIVE controller idiom (owner ruling 2026-08-16:
    /// follow the game's expectations — the WP6 arrow-key adjust layer is
    /// deleted, freeing the arrows for the review layer).
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
    ///    the drain reads a fresh value the same frame.
    ///
    /// Also hosts the slider-row describer used by the Pump's selection
    /// composition ("Header: Value, plus/minus" + queued description).
    /// Seam-gated (WP8); all game handles live in the Seams registry.
    /// </summary>
    [HarmonyPatch]
    public static class SliderArrowPatch
    {
        // Per-runtime-type getDescription lookup (polymorphic — stays local,
        // not a fixed seam; a type with no getDescription caches null).
        private static readonly Dictionary<Type, MethodInfo> _descMethods = new Dictionary<Type, MethodInfo>();

        private static bool RowReadReady =>
            Seams.SliderButton_headerTextBlock != null
            && Seams.SliderButton_currentValueTextBlock != null
            && Seams.UITextBlock_content != null;

        [HarmonyPrepare]
        static bool Prepare()
        {
            if (Seams.UITextSliderControl_update == null
                || Seams.UITextSliderControl_hoverButton == null
                || Seams.SkaldIO_getMouseUp == null)
            {
                Plugin.Logger?.LogWarning("[SliderArrow] click-join seams missing — slider values unvoiced");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.UITextSliderControl_update;

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
                if (!(bool)Seams.SkaldIO_getMouseUp.Invoke(null, new object[] { 0 })) return;
                object hoverButton = Seams.UITextSliderControl_hoverButton.GetValue(__instance);
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
            [HarmonyPrepare]
            static bool Prepare()
            {
                if (Seams.SliderButton_controllerScrollSidewaysLeft == null
                    && Seams.SliderButton_controllerScrollSidewaysRight == null)
                {
                    Plugin.Logger?.LogWarning("[SliderArrow] controllerScrollSideways seams missing — arrow flips unvoiced");
                    return false;
                }
                return true;
            }

            [HarmonyTargetMethods]
            static IEnumerable<MethodBase> TargetMethods()
            {
                if (Seams.SliderButton_controllerScrollSidewaysLeft != null)
                    yield return Seams.SliderButton_controllerScrollSidewaysLeft;
                if (Seams.SliderButton_controllerScrollSidewaysRight != null)
                    yield return Seams.SliderButton_controllerScrollSidewaysRight;
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
                if (Seams.SliderButtonType == null || element == null) return null;
                if (Seams.SliderButtonType.IsInstanceOfType(element)) return element;
                if (Seams.SliderButton_minusButton == null || Seams.SliderButton_plusButton == null
                    || Seams.UICanvas_getElements == null) return null;

                var elements = Seams.UICanvas_getElements.Invoke(control, null) as System.Collections.IEnumerable;
                if (elements == null) return null;
                foreach (var e in elements)
                {
                    if (e == null || !Seams.SliderButtonType.IsInstanceOfType(e)) continue;
                    if (ReferenceEquals(Seams.SliderButton_minusButton.GetValue(e), element)
                        || ReferenceEquals(Seams.SliderButton_plusButton.GetValue(e), element))
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
                if (Seams.SliderButton_controllerSelectPlusButton == null) return null;
                if (Seams.SliderButtonType == null || !Seams.SliderButtonType.IsInstanceOfType(button)) return null;
                return (bool)Seams.SliderButton_controllerSelectPlusButton.GetValue(button) ? "plus" : "minus";
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
                if (!RowReadReady) return null;
                if (Seams.SliderButtonType == null || !Seams.SliderButtonType.IsInstanceOfType(button)) return null;

                object headerBlock = Seams.SliderButton_headerTextBlock.GetValue(button);
                object valueBlock = Seams.SliderButton_currentValueTextBlock.GetValue(button);
                string header = headerBlock != null ? Seams.UITextBlock_content.GetValue(headerBlock) as string : null;
                string value = valueBlock != null ? Seams.UITextBlock_content.GetValue(valueBlock) as string : null;
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
                if (Seams.SliderSettingsButton_setting != null
                    && Seams.SkaldBaseObject_getFullDescription != null
                    && Seams.SliderSettingsButtonType != null
                    && Seams.SliderSettingsButtonType.IsInstanceOfType(button))
                {
                    object setting = Seams.SliderSettingsButton_setting.GetValue(button);
                    if (setting != null)
                        desc = Seams.SkaldBaseObject_getFullDescription.Invoke(setting, null) as string;
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
                // The row's description is the review panel while this row is
                // focused (WP10) — raw, so the tag grammar can section it.
                if (!string.IsNullOrWhiteSpace(desc)) ReviewLayer.NotePanel(desc);
                string cleaned = string.IsNullOrWhiteSpace(desc) ? null : TextCleaner.CleanText(desc);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    Scaffold.SpeechService.SayQueued(cleaned, "SliderDesc");
            }
            catch { }
        }
    }
}

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Event-driven speech for game content. Replaces the blanket UITextBlock.setContent()
    /// hook (TextInterceptPatch) with targeted postfixes on the specific GUIControl and
    /// PopUpBase methods that represent actual content changes.
    ///
    /// Each hook fires when a state class pushes new content — not when the renderer
    /// refreshes the same content 60 times per second. Per-source dedup ensures the
    /// same content arriving repeatedly (from setGUIData called every frame) only speaks once.
    ///
    /// No suppression windows. No seen-text sets. No mode filtering. No frame timers.
    /// </summary>
    public static class ContentSpeechPatch
    {
        private static readonly Dictionary<string, string> _lastSpoken = new Dictionary<string, string>();

        /// <summary>
        /// Clear all dedup state. Called on state transitions and popup dismissal.
        /// </summary>
        public static void ClearAll()
        {
            _lastSpoken.Clear();
        }

        /// <summary>
        /// Speak text if it differs from the last spoken text for this source.
        /// </summary>
        private static void SpeakIfChanged(string raw, string source, bool interrupt)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            string cleaned = TextInterceptPatch.CleanText(raw);
            if (string.IsNullOrWhiteSpace(cleaned)) return;

            _lastSpoken.TryGetValue(source, out string prev);
            if (cleaned == prev) return;
            _lastSpoken[source] = cleaned;

            if (interrupt)
                Plugin.Speech?.Speak(cleaned, source);
            else
                Plugin.Speech?.SpeakQueued(cleaned, source);

            Plugin.Logger?.LogInfo($"[Content:{source}] \"{cleaned}\"");
        }

        // =====================================================================
        // GROUP 1: GUIControl content methods
        // =====================================================================

        /// <summary>
        /// Dialogue/scene body text. Fires once per scene node entry.
        /// </summary>
        [HarmonyPatch(typeof(GUIControl), "setSceneDescription")]
        public static class SceneDescriptionHook
        {
            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "SceneDesc", interrupt: true);
            }
        }

        /// <summary>
        /// Context/status text — combat hover, location description, item description, etc.
        /// Called every frame on some screens; dedup catches same-content repeats.
        /// </summary>
        [HarmonyPatch(typeof(GUIControl), "setSecondaryDescription")]
        public static class SecondaryDescriptionHook
        {
            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "SecondaryDesc", interrupt: false);
            }
        }

        /// <summary>
        /// Area/encounter title.
        /// </summary>
        [HarmonyPatch(typeof(GUIControl), "setPrimaryHeader")]
        public static class PrimaryHeaderHook
        {
            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "PrimaryHeader", interrupt: false);
            }
        }

        /// <summary>
        /// Large screen title (main menu, etc).
        /// </summary>
        [HarmonyPatch(typeof(GUIControl), "setBigHeader")]
        public static class BigHeaderHook
        {
            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "BigHeader", interrupt: true);
            }
        }

        /// <summary>
        /// Right-column detail panel on sheet screens — class/ability/item/setting descriptions.
        /// Called every frame; dedup catches same-content repeats.
        /// </summary>
        [HarmonyPatch(typeof(GUIControl), "setSheetDescription")]
        public static class SheetDescriptionHook
        {
            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "SheetDesc", interrupt: false);
            }
        }

        /// <summary>
        /// Sheet title ("Pick a Difficulty", "Key Bindings", "Select a Class").
        /// </summary>
        [HarmonyPatch(typeof(GUIControl), "setSheetHeader")]
        public static class SheetHeaderHook
        {
            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "SheetHeader", interrupt: false);
            }
        }

        /// <summary>
        /// Context-sensitive action label ("Begin Combat!", "End Turn").
        /// </summary>
        [HarmonyPatch(typeof(GUIControl), "setContextualButton")]
        public static class ContextualButtonHook
        {
            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "ContextualButton", interrupt: false);
            }
        }

        // =====================================================================
        // GROUP 2: Tooltip
        // =====================================================================

        /// <summary>
        /// Tooltip text from right-click/Right Shift inspect.
        /// Hooked via HarmonyTargetMethod since ToolTipPrinter is resolved by name.
        /// </summary>
        [HarmonyPatch]
        public static class TooltipHook
        {
            [HarmonyTargetMethod]
            static MethodBase TargetMethod()
            {
                var type = AccessTools.TypeByName("ToolTipPrinter");
                if (type == null)
                {
                    Plugin.Logger?.LogWarning("[Content] ToolTipPrinter not found");
                    return null;
                }
                var method = AccessTools.Method(type, "setToolTip",
                    new[] { typeof(string), AccessTools.TypeByName("ToolTipControl+ToolTipCategory") });
                if (method == null)
                {
                    Plugin.Logger?.LogWarning("[Content] ToolTipPrinter.setToolTip not found");
                    return null;
                }
                Plugin.Logger?.LogInfo("[Content] Found ToolTipPrinter.setToolTip for patching");
                return method;
            }

            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "Tooltip", interrupt: true);
            }
        }

        // =====================================================================
        // GROUP 3: Popup dynamic updates
        // =====================================================================

        /// <summary>
        /// Popup main text updates during interaction.
        /// Hooked on PopUpBase (outer class) which delegates to PopUpUIBase.
        /// </summary>
        [HarmonyPatch]
        public static class PopupMainTextHook
        {
            [HarmonyTargetMethod]
            static MethodBase TargetMethod()
            {
                var type = AccessTools.TypeByName("PopUpBase");
                if (type == null) return null;
                var method = AccessTools.Method(type, "setMainTextContent", new[] { typeof(string) });
                if (method != null) Plugin.Logger?.LogInfo("[Content] Found PopUpBase.setMainTextContent for patching");
                return method;
            }

            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "PopupMain", interrupt: true);
            }
        }

        /// <summary>
        /// Popup secondary text updates (lock description, rest progress, etc).
        /// </summary>
        [HarmonyPatch]
        public static class PopupSecondaryTextHook
        {
            [HarmonyTargetMethod]
            static MethodBase TargetMethod()
            {
                var type = AccessTools.TypeByName("PopUpBase");
                if (type == null) return null;
                var method = AccessTools.Method(type, "setSecondaryTextContent", new[] { typeof(string) });
                if (method != null) Plugin.Logger?.LogInfo("[Content] Found PopUpBase.setSecondaryTextContent for patching");
                return method;
            }

            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "PopupSecondary", interrupt: false);
            }
        }

        /// <summary>
        /// Popup tertiary text updates (lockpick results, hovered item/spell name,
        /// text input, skill check outcomes, rest progress).
        /// </summary>
        [HarmonyPatch]
        public static class PopupTertiaryTextHook
        {
            [HarmonyTargetMethod]
            static MethodBase TargetMethod()
            {
                var type = AccessTools.TypeByName("PopUpBase");
                if (type == null) return null;
                var method = AccessTools.Method(type, "setTertiaryTextContent", new[] { typeof(string) });
                if (method != null) Plugin.Logger?.LogInfo("[Content] Found PopUpBase.setTertiaryTextContent for patching");
                return method;
            }

            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "PopupTertiary", interrupt: false);
            }
        }
    }
}

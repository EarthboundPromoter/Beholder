using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Event-driven speech for game content. Replaces the old blanket
    /// UITextBlock.setContent() hook with targeted postfixes on the specific
    /// GUIControl and PopUpBase methods that represent actual content changes.
    ///
    /// Note-only since WP5: several of these setters are called every frame by
    /// setGUIData, so the hooks note into the Pump — latest value per source wins
    /// within the frame, and the drain diffs against the last drained value at
    /// the clock. Every hook is seam-gated through the WP8 registry: a setter
    /// the game renames costs that one source, logged and counted at boot.
    /// </summary>
    public static class ContentSpeechPatch
    {
        private static void SpeakIfChanged(string raw, string source, bool interrupt)
        {
            Pump.NoteContent(source, raw, interrupt);
        }

        // =====================================================================
        // GROUP 1: GUIControl content methods
        // =====================================================================

        /// <summary>
        /// Dialogue/scene body text. Fires once per scene node entry.
        /// </summary>
        [HarmonyPatch]
        public static class SceneDescriptionHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.GUIControl_setSceneDescription != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_setSceneDescription;

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
        [HarmonyPatch]
        public static class SecondaryDescriptionHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.GUIControl_setSecondaryDescription != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_setSecondaryDescription;

            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "SecondaryDesc", interrupt: false);
            }
        }

        /// <summary>
        /// Area/encounter title.
        /// </summary>
        [HarmonyPatch]
        public static class PrimaryHeaderHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.GUIControl_setPrimaryHeader != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_setPrimaryHeader;

            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "PrimaryHeader", interrupt: false);
            }
        }

        /// <summary>
        /// Large screen title (main menu, etc).
        /// </summary>
        [HarmonyPatch]
        public static class BigHeaderHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.GUIControl_setBigHeader != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_setBigHeader;

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
        [HarmonyPatch]
        public static class SheetDescriptionHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.GUIControl_setSheetDescription != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_setSheetDescription;

            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "SheetDesc", interrupt: false);
            }
        }

        /// <summary>
        /// Sheet title ("Pick a Difficulty", "Key Bindings", "Select a Class").
        /// </summary>
        [HarmonyPatch]
        public static class SheetHeaderHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.GUIControl_setSheetHeader != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_setSheetHeader;

            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "SheetHeader", interrupt: false);
            }
        }

        /// <summary>
        /// Context-sensitive action label ("Begin Combat!", "End Turn").
        /// </summary>
        [HarmonyPatch]
        public static class ContextualButtonHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.GUIControl_setContextualButton != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_setContextualButton;

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
        /// </summary>
        [HarmonyPatch]
        public static class TooltipHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Seams.ToolTipPrinter_setToolTip != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.ToolTipPrinter_setToolTip;

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
            [HarmonyPrepare]
            static bool Prepare() => Seams.PopUpBase_setMainTextContent != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.PopUpBase_setMainTextContent;

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
            [HarmonyPrepare]
            static bool Prepare() => Seams.PopUpBase_setSecondaryTextContent != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.PopUpBase_setSecondaryTextContent;

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
            [HarmonyPrepare]
            static bool Prepare() => Seams.PopUpBase_setTertiaryTextContent != null;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.PopUpBase_setTertiaryTextContent;

            [HarmonyPostfix]
            static void Postfix(string __0)
            {
                SpeakIfChanged(__0, "PopupTertiary", interrupt: false);
            }
        }
    }
}

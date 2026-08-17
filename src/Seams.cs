using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SkaldAccessibility
{
    /// <summary>
    /// WP8: the seam registry and boot audit. Every game-side anchor the mod
    /// depends on — patch-target methods, drain-side reflection reads, and
    /// name-matched types — resolves HERE, once, eagerly at Awake; every
    /// consumer reads the resolved handle. A game update that renames or
    /// removes a seam becomes: that one stream disabled (its patch class
    /// Prepare()s false, its drain consumer null-checks), a log line per
    /// missing row, and one spoken boot line — never a silent failure.
    ///
    /// Why the gating is load-bearing and not just tidy: Harmony throws when a
    /// TargetMethod resolves null (or a TargetMethods yield comes up empty),
    /// and PatchAll's type loop has no per-class isolation — one missing seam
    /// would abort every patch class after it. Verified against the shipped
    /// 0Harmony.dll (PatchClassProcessor.PatchWithAttributes / GetBulkMethods /
    /// ReportException; Harmony.PatchAll), 2026-08-16. Plugin.Awake therefore
    /// patches class-by-class with a try/catch, and Prepare() gates make the
    /// expected missing-seam path exception-free.
    ///
    /// METADATA ONLY here: Type / MethodInfo / FieldInfo / PropertyInfo.
    /// Never GetValue or Invoke at resolve time — value reads on game statics
    /// run static constructors, which need game data loaded (the WP7 frame-0
    /// ConsoleControl boot-kill). Consumers read values lazily, post-ready,
    /// through these handles (TagValue below for the C64Color markup tags).
    ///
    /// Mod-internal contract this audit cannot cover: SkaldBridge arms
    /// synthetic input by reflecting into SkaldIOPatches' Inject*Frame fields
    /// by name — keep those names in sync with the bridge source (bridge\).
    /// </summary>
    internal static class Seams
    {
        // ---- Audit state ----
        private static readonly List<string> _missing = new List<string>();
        private static readonly List<string> _patchFailures = new List<string>();
        private static int _rowCount;

        // ---- Core types ----
        internal static Type SkaldIOType;
        internal static Type ControllerInputControlType;   // SkaldIO+ControllerInputControl
        internal static Type StateControlType;             // MainControl+StateControl
        internal static Type StateBaseType;
        internal static Type UICanvasType;
        internal static Type UIButtonControlBaseType;
        internal static Type SkaldObjectListType;
        internal static Type SkaldBaseObjectType;
        internal static Type PopUpControlType;
        internal static Type PopUpBaseType;
        internal static Type PopUpUIBaseType;              // PopUpBase+PopUpUIBase
        internal static Type ToolTipPrinterType;
        internal static Type ToolTipCategoryType;          // ToolTipControl+ToolTipCategory
        internal static Type BarkType;                     // BarkControl+Bark (non-public nested)
        internal static Type UITextSliderControlType;
        internal static Type SliderButtonType;             // UITextSliderControl+UITextSliderButton
        internal static Type SliderSettingsButtonType;     // UITextSliderControl+UITextSliderSettingsButton
        internal static Type SheetComplexSettingsType;     // GUIControl+SheetComplexSettings
        internal static Type FeatNodeType;                 // UIFeatTree+FeatTreeCollection+Node
        internal static Type FeatType;                     // FeatContainer+Feat
        internal static Type C64ColorType;
        internal static Type ConsoleControlType;
        internal static Type FeedbackToolType;

        // Text-entry popups (the three getInputString consumers — the
        // ControllerFeed gate matches these by type; a game update adding a
        // fourth text popup is a new row here).
        internal static Type PopUpNameType;
        internal static Type PopUpCreateSaveType;
        internal static Type PopUpSaveRenameType;

        // Numeric-class button rows (keep their leading "N:" shortcut label in
        // selection composition instead of a trailing browse counter).
        internal static Type SheetButtonControlType;
        internal static Type NumericButtonControlType;
        internal static Type MenuButtonControlType;

        // ---- Input layer (SkaldIOPatches / ControllerFeedPatch) ----
        internal static MethodInfo SkaldIO_isControllerConnected;
        internal static MethodInfo SkaldIO_getKeyPressed;
        internal static MethodInfo SkaldIO_getKeyHeldDown;
        internal static MethodInfo SkaldIO_getKeyUp;
        internal static MethodInfo SkaldIO_getPressedEscapeKey;
        internal static MethodInfo SkaldIO_getMouseUp;
        internal static MethodInfo GUIControl_setMouseToClosestOptionAbove;
        internal static MethodInfo GUIControl_setMouseToClosestOptionBelow;
        internal static MethodInfo GUIControl_getControllerScrollableList;
        internal static MethodInfo UICanvas_canControllerScrollUp;
        internal static MethodInfo UICanvas_canControllerScrollDown;
        internal static MethodInfo SheetComplexSettings_getControllerScrollableList;
        internal static MethodInfo SheetComplexSettings_getListButtons;
        internal static FieldInfo ConsoleControl_console;
        internal static FieldInfo FeedbackTool_takeInput;
        internal static MethodInfo PopUpControl_getCurrentPopUp;

        /// <summary>The 19 ControllerInputControl accessors the keyboard feed
        /// ORs into, keyed by accessor name (ControllerFeedPatch consumes).</summary>
        internal static readonly Dictionary<string, MethodInfo> FeedAccessors
            = new Dictionary<string, MethodInfo>();
        internal static readonly string[] FeedAccessorNames =
        {
            "buttonBPressed", "buttonXPressed", "buttonYPressed",
            "leftBumperPressed", "rightBumperPressed",
            "leftTriggerPressed", "leftTriggerHeld", "leftTriggerUp",
            "rightTriggerPressed", "rightTriggerHeld", "rightTriggerUp",
            "isLeftStickUpPressed", "isLeftStickUpHeld",
            "isLeftStickDownPressed", "isLeftStickDownHeld",
            "isLeftStickLeftPressed", "isLeftStickLeftHeld",
            "isLeftStickRightPressed", "isLeftStickRightHeld",
        };

        // ---- State clock ----
        internal static MethodInfo StateControl_setState;
        internal static FieldInfo StateControl_currentState;
        internal static FieldInfo StateBase_guiControl;
        internal static FieldInfo GUIControl_numericButtons;

        // ---- Selection / navigation joins ----
        internal static MethodInfo UICanvas_setCurrentSelectedButton;
        internal static FieldInfo UICanvas_currentSelectedButton;
        internal static MethodInfo UICanvas_getScrollableElements;
        internal static MethodInfo UICanvas_getElements;
        internal static MethodInfo UIButtonControlBase_getButtonsList;
        internal static FieldInfo UITextBlock_content;
        internal static MethodInfo SkaldObjectList_setCurrentObject;
        internal static MethodInfo SkaldObjectList_getObjectByIndex;
        internal static MethodInfo SkaldObjectList_getCurrentObject;
        internal static MethodInfo SkaldBaseObject_getListName;   // getListName, else getName
        internal static MethodInfo SkaldBaseObject_getFullDescription;
        internal static MethodInfo PopUpUIBase_setControllerScrollableUICanvas;
        internal static FieldInfo FeatNode_feat;
        internal static MethodInfo Feat_getName;

        // ---- Content sources ----
        internal static MethodInfo GUIControl_setSceneDescription;
        internal static MethodInfo GUIControl_setSecondaryDescription;
        internal static MethodInfo GUIControl_setPrimaryHeader;
        internal static MethodInfo GUIControl_setBigHeader;
        internal static MethodInfo GUIControl_setSheetDescription;
        internal static MethodInfo GUIControl_setSheetHeader;
        internal static MethodInfo GUIControl_setContextualButton;
        internal static MethodInfo ToolTipPrinter_setToolTip;
        internal static MethodInfo PopUpBase_setMainTextContent;
        internal static MethodInfo PopUpBase_setSecondaryTextContent;
        internal static MethodInfo PopUpBase_setTertiaryTextContent;
        internal static MethodInfo CombatLog_addEntry;
        internal static ConstructorInfo Bark_ctor;

        // ---- Popup announce ----
        internal static MethodInfo PopUpControl_addPopUp;
        internal static FieldInfo PopUpBase_uiElements;
        internal static FieldInfo PopUpUIBase_mainDescription;
        internal static FieldInfo PopUpUIBase_secondaryDescription;
        internal static FieldInfo PopUpUIBase_tertiaryDescription;

        // ---- Sliders ----
        internal static MethodInfo UITextSliderControl_update;
        internal static FieldInfo UITextSliderControl_hoverButton;
        internal static MethodInfo SliderButton_controllerScrollSidewaysLeft;
        internal static MethodInfo SliderButton_controllerScrollSidewaysRight;
        internal static FieldInfo SliderButton_headerTextBlock;
        internal static FieldInfo SliderButton_currentValueTextBlock;
        internal static FieldInfo SliderButton_controllerSelectPlusButton;
        internal static FieldInfo SliderButton_minusButton;
        internal static FieldInfo SliderButton_plusButton;
        internal static FieldInfo SliderSettingsButton_setting;

        // ---- C64Color markup tags (metadata only — value reads are lazy,
        //      post-ready, via TagValue) ----
        internal static MemberInfo C64_YellowTag;
        internal static MemberInfo C64_HeaderTag;
        internal static MemberInfo C64_AttributeNameTag;
        internal static MemberInfo C64_AttributeValueTag;
        internal static MemberInfo C64_GreenLightTag;
        internal static MemberInfo C64_RedLightTag;

        /// <summary>Resolve the whole manifest. Called once from Plugin.Awake,
        /// before any patching. Metadata reads only — safe at frame 0.</summary>
        internal static void ResolveAll()
        {
            // Types
            SkaldIOType = T("SkaldIO");
            ControllerInputControlType = T("SkaldIO+ControllerInputControl");
            StateControlType = T("MainControl+StateControl");
            StateBaseType = T("StateBase");
            UICanvasType = T("UICanvas");
            UIButtonControlBaseType = T("UIButtonControlBase");
            SkaldObjectListType = T("SkaldObjectList");
            SkaldBaseObjectType = T("SkaldBaseObject");
            PopUpControlType = T("PopUpControl");
            PopUpBaseType = T("PopUpBase");
            PopUpUIBaseType = T("PopUpBase+PopUpUIBase");
            ToolTipPrinterType = T("ToolTipPrinter");
            ToolTipCategoryType = T("ToolTipControl+ToolTipCategory");
            UITextSliderControlType = T("UITextSliderControl");
            SliderButtonType = T("UITextSliderControl+UITextSliderButton");
            SliderSettingsButtonType = T("UITextSliderControl+UITextSliderSettingsButton");
            SheetComplexSettingsType = T("GUIControl+SheetComplexSettings");
            FeatNodeType = T("UIFeatTree+FeatTreeCollection+Node");
            FeatType = T("FeatContainer+Feat");
            C64ColorType = T("C64Color");
            ConsoleControlType = T("ConsoleControl");
            FeedbackToolType = T("FeedbackTool");
            PopUpNameType = T("PopUpName");
            PopUpCreateSaveType = T("PopUpCreateSave");
            PopUpSaveRenameType = T("PopUpSaveRename");
            SheetButtonControlType = T("SheetButtonControl");
            NumericButtonControlType = T("NumericButtonControl");
            MenuButtonControlType = T("MenuButtonControl");

            // BarkControl+Bark is non-public — GetNestedType, not TypeByName.
            var barkControl = T("BarkControl");
            BarkType = barkControl?.GetNestedType("Bark", BindingFlags.NonPublic | BindingFlags.Public);
            Row("BarkControl+Bark", BarkType != null);

            // Input layer
            SkaldIO_isControllerConnected = M(SkaldIOType, "SkaldIO", "isControllerConnected");
            SkaldIO_getKeyPressed = M(SkaldIOType, "SkaldIO", "getKeyPressed", new[] { typeof(UnityEngine.KeyCode) });
            SkaldIO_getKeyHeldDown = M(SkaldIOType, "SkaldIO", "getKeyHeldDown", new[] { typeof(UnityEngine.KeyCode) });
            SkaldIO_getKeyUp = M(SkaldIOType, "SkaldIO", "getKeyUp", new[] { typeof(UnityEngine.KeyCode) });
            SkaldIO_getPressedEscapeKey = M(SkaldIOType, "SkaldIO", "getPressedEscapeKey");
            SkaldIO_getMouseUp = M(SkaldIOType, "SkaldIO", "getMouseUp", new[] { typeof(int) });
            GUIControl_setMouseToClosestOptionAbove = M(typeof(GUIControl), "GUIControl", "setMouseToClosestOptionAbove");
            GUIControl_setMouseToClosestOptionBelow = M(typeof(GUIControl), "GUIControl", "setMouseToClosestOptionBelow");
            GUIControl_getControllerScrollableList = M(typeof(GUIControl), "GUIControl", "getControllerScrollableList");
            UICanvas_canControllerScrollUp = M(UICanvasType, "UICanvas", "canControllerScrollUp");
            UICanvas_canControllerScrollDown = M(UICanvasType, "UICanvas", "canControllerScrollDown");
            SheetComplexSettings_getControllerScrollableList = M(SheetComplexSettingsType, "SheetComplexSettings", "getControllerScrollableList");
            SheetComplexSettings_getListButtons = M(SheetComplexSettingsType, "SheetComplexSettings", "getListButtons");
            ConsoleControl_console = F(ConsoleControlType, "ConsoleControl", "console");
            FeedbackTool_takeInput = F(FeedbackToolType, "FeedbackTool", "takeInput");
            PopUpControl_getCurrentPopUp = M(PopUpControlType, "PopUpControl", "getCurrentPopUp");

            FeedAccessors.Clear();
            foreach (string name in FeedAccessorNames)
                FeedAccessors[name] = M(ControllerInputControlType, "ControllerInputControl", name);

            // State clock
            StateControl_setState = M(StateControlType, "StateControl", "setState");
            StateControl_currentState = F(StateControlType, "StateControl", "currentState");
            StateBase_guiControl = F(StateBaseType, "StateBase", "guiControl");
            GUIControl_numericButtons = F(typeof(GUIControl), "GUIControl", "numericButtons");

            // Selection / navigation
            UICanvas_setCurrentSelectedButton = M(UICanvasType, "UICanvas", "setCurrentSelectedButton");
            UICanvas_currentSelectedButton = F(UICanvasType, "UICanvas", "currentSelectedButton");
            UICanvas_getScrollableElements = M(UICanvasType, "UICanvas", "getScrollableElements");
            UICanvas_getElements = M(UICanvasType, "UICanvas", "getElements");
            UIButtonControlBase_getButtonsList = M(UIButtonControlBaseType, "UIButtonControlBase", "getButtonsList");
            UITextBlock_content = F(typeof(UITextBlock), "UITextBlock", "content");
            SkaldObjectList_setCurrentObject = M(SkaldObjectListType, "SkaldObjectList", "setCurrentObject");
            SkaldObjectList_getObjectByIndex = M(SkaldObjectListType, "SkaldObjectList", "getObjectByIndex");
            SkaldObjectList_getCurrentObject = M(SkaldObjectListType, "SkaldObjectList", "getCurrentObject");
            SkaldBaseObject_getListName = SkaldBaseObjectType == null ? null
                : AccessTools.Method(SkaldBaseObjectType, "getListName")
                    ?? AccessTools.Method(SkaldBaseObjectType, "getName");
            Row("SkaldBaseObject.getListName|getName", SkaldBaseObject_getListName != null);
            SkaldBaseObject_getFullDescription = M(SkaldBaseObjectType, "SkaldBaseObject", "getFullDescription");
            PopUpUIBase_setControllerScrollableUICanvas = M(PopUpUIBaseType, "PopUpUIBase", "setControllerScrollableUICanvas");
            FeatNode_feat = F(FeatNodeType, "FeatTreeCollection.Node", "feat");
            Feat_getName = M(FeatType, "Feat", "getName");

            // Content sources
            GUIControl_setSceneDescription = M(typeof(GUIControl), "GUIControl", "setSceneDescription");
            GUIControl_setSecondaryDescription = M(typeof(GUIControl), "GUIControl", "setSecondaryDescription");
            GUIControl_setPrimaryHeader = M(typeof(GUIControl), "GUIControl", "setPrimaryHeader");
            GUIControl_setBigHeader = M(typeof(GUIControl), "GUIControl", "setBigHeader");
            GUIControl_setSheetDescription = M(typeof(GUIControl), "GUIControl", "setSheetDescription");
            GUIControl_setSheetHeader = M(typeof(GUIControl), "GUIControl", "setSheetHeader");
            GUIControl_setContextualButton = M(typeof(GUIControl), "GUIControl", "setContextualButton");
            ToolTipPrinter_setToolTip = ToolTipPrinterType == null || ToolTipCategoryType == null ? null
                : AccessTools.Method(ToolTipPrinterType, "setToolTip", new[] { typeof(string), ToolTipCategoryType });
            Row("ToolTipPrinter.setToolTip", ToolTipPrinter_setToolTip != null);
            PopUpBase_setMainTextContent = M(PopUpBaseType, "PopUpBase", "setMainTextContent", new[] { typeof(string) });
            PopUpBase_setSecondaryTextContent = M(PopUpBaseType, "PopUpBase", "setSecondaryTextContent", new[] { typeof(string) });
            PopUpBase_setTertiaryTextContent = M(PopUpBaseType, "PopUpBase", "setTertiaryTextContent", new[] { typeof(string) });
            CombatLog_addEntry = M(typeof(CombatLog), "CombatLog", "addEntry", new[] { typeof(string), typeof(string) });
            Bark_ctor = BarkType == null ? null
                : AccessTools.Constructor(BarkType, new[]
                  {
                      typeof(string), typeof(int), typeof(int),
                      typeof(UnityEngine.Color), typeof(UnityEngine.Color), typeof(int)
                  });
            Row("Bark..ctor", Bark_ctor != null);

            // Popup announce
            PopUpControl_addPopUp = PopUpControlType == null || PopUpBaseType == null ? null
                : AccessTools.Method(PopUpControlType, "addPopUp", new[] { PopUpBaseType });
            Row("PopUpControl.addPopUp", PopUpControl_addPopUp != null);
            PopUpBase_uiElements = F(PopUpBaseType, "PopUpBase", "uiElements");
            PopUpUIBase_mainDescription = F(PopUpUIBaseType, "PopUpUIBase", "mainDescription");
            PopUpUIBase_secondaryDescription = F(PopUpUIBaseType, "PopUpUIBase", "secondaryDescription");
            PopUpUIBase_tertiaryDescription = F(PopUpUIBaseType, "PopUpUIBase", "tertiaryDescription");

            // Sliders
            UITextSliderControl_update = M(UITextSliderControlType, "UITextSliderControl", "update");
            UITextSliderControl_hoverButton = F(UITextSliderControlType, "UITextSliderControl", "hoverButton");
            SliderButton_controllerScrollSidewaysLeft = M(SliderButtonType, "UITextSliderButton", "controllerScrollSidewaysLeft");
            SliderButton_controllerScrollSidewaysRight = M(SliderButtonType, "UITextSliderButton", "controllerScrollSidewaysRight");
            SliderButton_headerTextBlock = F(SliderButtonType, "UITextSliderButton", "headerTextBlock");
            SliderButton_currentValueTextBlock = F(SliderButtonType, "UITextSliderButton", "currentValueTextBlock");
            SliderButton_controllerSelectPlusButton = F(SliderButtonType, "UITextSliderButton", "controllerSelectPlusButton");
            SliderButton_minusButton = F(SliderButtonType, "UITextSliderButton", "minusButton");
            SliderButton_plusButton = F(SliderButtonType, "UITextSliderButton", "plusButton");
            SliderSettingsButton_setting = F(SliderSettingsButtonType, "UITextSliderSettingsButton", "setting");

            // C64Color tags
            C64_YellowTag = PF(C64ColorType, "C64Color", "YELLOW_TAG");
            C64_HeaderTag = PF(C64ColorType, "C64Color", "HEADER_TAG");
            C64_AttributeNameTag = PF(C64ColorType, "C64Color", "ATTRIBUTE_NAME_TAG");
            C64_AttributeValueTag = PF(C64ColorType, "C64Color", "ATTRIBUTE_VALUE_TAG");
            C64_GreenLightTag = PF(C64ColorType, "C64Color", "GREEN_LIGHT_TAG");
            C64_RedLightTag = PF(C64ColorType, "C64Color", "RED_LIGHT_TAG");
        }

        /// <summary>A C64Color tag's string value. LAZY — call only post-ready
        /// (composition paths), never at Awake: the getter may run the type's
        /// static constructor, which needs game data.</summary>
        internal static string TagValue(MemberInfo tag)
        {
            try
            {
                if (tag is PropertyInfo p) return p.GetValue(null, null) as string;
                if (tag is FieldInfo f) return f.GetValue(null) as string;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[Seams] Tag read failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>Record a patch class that threw during application (the
        /// per-class isolation loop in Plugin.Awake catches it).</summary>
        internal static void NotePatchFailure(string patchClass, Exception ex)
        {
            _patchFailures.Add(patchClass);
            Plugin.Logger?.LogError($"[Seams] Patch class failed: {patchClass} — {ex.Message}");
        }

        /// <summary>Log every missing row and speak the one boot line when
        /// anything is gone. Called from Plugin.Awake after patching.</summary>
        internal static void Report()
        {
            foreach (string row in _missing)
                Plugin.Logger?.LogError($"[Seams] Missing seam: {row}");

            int n = _missing.Count + _patchFailures.Count;
            if (n == 0)
            {
                Plugin.Logger?.LogInfo($"[Seams] All {_rowCount} seams resolved.");
                return;
            }
            Plugin.Logger?.LogError($"[Seams] {n} of {_rowCount} seam rows missing or failed.");
            Scaffold.SpeechService.SayQueued(
                n == 1 ? "1 game hook missing after an update."
                       : $"{n} game hooks missing after an update.", "Init");
        }

        // ---- Resolution helpers (each records one audit row) ----

        private static Type T(string name)
        {
            var t = AccessTools.TypeByName(name);
            Row(name, t != null);
            return t;
        }

        private static MethodInfo M(Type type, string owner, string name, Type[] args = null)
        {
            var m = type == null ? null : AccessTools.Method(type, name, args);
            Row($"{owner}.{name}", m != null);
            return m;
        }

        private static FieldInfo F(Type type, string owner, string name)
        {
            var f = type == null ? null : AccessTools.Field(type, name);
            Row($"{owner}.{name}", f != null);
            return f;
        }

        private static MemberInfo PF(Type type, string owner, string name)
        {
            MemberInfo m = type == null ? null
                : (MemberInfo)AccessTools.Property(type, name) ?? AccessTools.Field(type, name);
            Row($"{owner}.{name}", m != null);
            return m;
        }

        private static void Row(string name, bool ok)
        {
            _rowCount++;
            if (!ok) _missing.Add(name);
        }
    }
}

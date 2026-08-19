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
        internal static MethodInfo GUIControl_getNumericButtonPressIndex;
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

        // ---- Popup announce (top-of-stack watch — the add event alone misses
        //      reveals and frame-late UI builds; the drain reads getCurrentPopUp,
        //      the game's own authoritative current-popup accessor) ----
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

        // ---- Selector grids (WP9) ----
        internal static Type UIAbilitySelectorGridType;
        internal static Type UICombatSelectorGridType;     // UICombatAbilitySelectorGrid
        internal static Type CombatPlanningStateType;
        internal static Type CombatBaseStateType;
        internal static Type OverlandStateType;
        internal static Type CharacterType;
        internal static Type ButtonDataType;               // UIButtonControlBase+ButtonData
        internal static FieldInfo GUIControl_abilitySelectorGrid;
        internal static MethodInfo GUIControl_setMouseToUIElement;
        // TP2 — the dialogue text cursor's three seams: the scene pane field,
        // its rendered topic-word list, and the funnel's snap-to-selected.
        internal static FieldInfo GUIControl_sceneDescription;
        internal static FieldInfo UITextBlock_toolTipWords;
        internal static MethodInfo GUIControl_setMouseToSelectedOption;
        internal static MethodInfo UICanvas_controllerScrollSidewaysLeft;
        internal static MethodInfo UICanvas_controllerScrollSidewaysRight;
        internal static MethodInfo SkaldIO_getOptionSelectionButtonUp;
        internal static MethodInfo SkaldIO_getOptionSelectionButtonDown;
        internal static MethodInfo SkaldIO_getOptionSelectionButtonLeft;
        internal static MethodInfo SkaldIO_getOptionSelectionButtonRight;
        internal static FieldInfo CombatPlanning_abilityGrid;
        internal static FieldInfo CombatPlanning_spellGrid;
        internal static FieldInfo CombatPlanning_consumableGrid;
        internal static FieldInfo Overland_spellGrid;
        internal static FieldInfo Overland_currentCharacter;
        internal static MethodInfo CombatBase_getCurrentCharacter;
        internal static MethodInfo Character_getCombatAbilityButtonData;
        internal static MethodInfo Character_getCombatSpellButtonData;
        internal static MethodInfo Character_getNonCombatSpellButtonData;
        internal static MethodInfo Character_getInventory;
        internal static MethodInfo Inventory_getConsumablesButtonData;
        internal static FieldInfo ButtonData_hoverText;
        internal static FieldInfo CombatSelectorGrid_textBlock;

        /// <summary>The eight movement accessors the modal-grid ruling
        /// suppresses while a selector grid is open (combat pressed-reads,
        /// overland held-reads), keyed by name (GridNavigationPatch consumes).</summary>
        internal static readonly Dictionary<string, MethodInfo> MovementReaders
            = new Dictionary<string, MethodInfo>();
        internal static readonly string[] MovementReaderNames =
        {
            "getPressedUpKey", "getPressedDownKey", "getPressedLeftKey", "getPressedRightKey",
            "getDownUpKey", "getDownDownKey", "getDownLeftKey", "getDownRightKey",
        };

        // ---- Overland cursor (WP11) ----
        internal static Type MainControlType;
        internal static Type DataControlType;
        internal static Type MapType;
        internal static Type MapTileType;
        internal static Type MapTileGridType;
        internal static Type MapIllustratorType;
        internal static Type ScrollControlType;            // MapIllustrator+ScrollControl
        internal static Type PartyType;
        internal static Type NavigationCourseType;
        internal static Type SKALDKeyBindingsType;
        internal static Type InventoryType;
        internal static MethodInfo MainControl_getDataControl;
        internal static FieldInfo DataControl_currentMap;
        internal static MethodInfo DataControl_getBuffer;
        internal static MethodInfo Map_getMouseTile;
        internal static MethodInfo Map_isScrollReady;
        internal static MethodInfo Map_getTile;
        internal static MethodInfo Map_isTileValid;
        internal static MethodInfo Map_getXPos;
        internal static MethodInfo Map_getYPos;
        internal static MethodInfo Map_getViewportX;
        internal static MethodInfo Map_getViewportY;
        internal static MethodInfo Map_findPathToMouseTile;
        internal static FieldInfo Map_playerParty;
        internal static FieldInfo Map_tileGrid;
        internal static FieldInfo Map_mapIllustrator;
        internal static FieldInfo Map_viewDistance;
        internal static FieldInfo Map_northernEdgeMapId;
        internal static FieldInfo Map_easternEdgeMapId;
        internal static FieldInfo Map_westernEdgeMapId;
        internal static FieldInfo Map_southernEdgeMapId;
        internal static MethodInfo MapTileGrid_testLineOfSight;
        internal static FieldInfo MapIllustrator_scrollControl;
        internal static FieldInfo ScrollControl_scrollX;
        internal static FieldInfo ScrollControl_scrollY;
        internal static MethodInfo MapTile_getLiveCharacter;
        internal static MethodInfo MapTile_getPropOrGuestProp;
        internal static MethodInfo MapTile_isSpotted;
        internal static MethodInfo MapTile_isIlluminated;
        internal static MethodInfo MapTile_isConcealment;
        internal static MethodInfo MapTile_isPassable;
        internal static MethodInfo MapTile_isWater;
        internal static MethodInfo MapTile_isVoidTile;
        internal static MethodInfo MapTile_getNestedMapId;
        internal static MethodInfo MapTile_getInventory;
        internal static MethodInfo MapTile_getVehicle;
        internal static MethodInfo MapTile_hasVehicle;
        internal static MethodInfo MapTile_getDeadParty;
        internal static MethodInfo MapTile_getInspectDescription;
        internal static MethodInfo MapTile_getVerb;
        internal static MethodInfo MapTile_getTileX;
        internal static MethodInfo MapTile_getTileY;
        internal static MethodInfo SkaldBaseObject_getName;
        internal static MethodInfo SkaldWorldObject_getTileX;
        internal static MethodInfo SkaldWorldObject_getTileY;
        internal static MethodInfo SkaldWorldObject_getContainerMapId;
        internal static MethodInfo Inventory_isEmpty;
        internal static MethodInfo Party_getObjectList;
        internal static MethodInfo Party_setNavigationCourse;
        internal static MethodInfo Party_clearNavigationCourse;
        internal static MethodInfo Party_navigationCourseHasNodes;
        internal static MethodInfo NavigationCourse_getLength;
        internal static MethodInfo Character_isHostile;
        internal static MethodInfo Character_isPC;
        internal static MethodInfo Character_isSpotted;
        internal static MethodInfo Prop_isHidden;
        internal static MethodInfo Prop_shouldNotBeDrawn;
        internal static MethodInfo Prop_shouldBeRemovedFromGame;   // the renderer's third gate (MapIllustrator drawProps)
        internal static MethodInfo PropLockable_isLocked;          // "empty" tag suppressed behind locks (owner ruling 2026-08-19)
        internal static MethodInfo SkaldIO_setVirtualMousePosition;
        internal static MethodInfo ToolTipPrinter_clearToolTip;
        internal static MethodInfo ToolTipPrinter_hasToolTip;
        internal static MethodInfo OverlandState_setMouseInput;
        internal static MethodInfo KeyBindings_getUpAltKey;
        internal static MethodInfo KeyBindings_getDownAltKey;
        internal static MethodInfo KeyBindings_getLeftAltKey;
        internal static MethodInfo KeyBindings_getRightAltKey;

        // Prop classification types (WP11 scan categories)
        internal static Type PropType;
        internal static Type PropDoorType;
        internal static Type PropContType;
        internal static Type PropWarpType;
        internal static Type PropPickupType;
        internal static Type PropDecorativeType;
        internal static Type PropBeaconType;
        internal static Type PropSpawnerType;
        internal static Type PropTriggerType;

        // ---- Character creation: point pools + feat tree (CC report build,
        //      2026-08-16 — feat lateral designation, points-remaining speech,
        //      buy/refund rank lines; FeatBuyState rides the same seams) ----
        internal static Type UIFeatTreeType;
        internal static Type FeatTreeCollectionType;   // UIFeatTree+FeatTreeCollection
        internal static Type FeatTreeType;             // UIFeatTree+FeatTreeCollection+FeatTree
        internal static Type UIAttributeEditorSheetType;
        internal static Type CharacterBuilderBaseStateType;
        internal static MethodInfo UIFeatTree_setPointsText;
        internal static FieldInfo UIFeatTree_treeCollection;
        internal static MethodInfo UIFeatTree_updatePressedLeftFeat;
        internal static MethodInfo UIFeatTree_updatePressedRightFeat;
        internal static FieldInfo UIFeatTree_pressedLeftFeat;
        internal static FieldInfo UIFeatTree_pressedRightFeat;
        internal static FieldInfo FeatTreeCollection_controllerScrollIndex;
        internal static FieldInfo FeatTree_nodeDictionary;
        internal static MethodInfo Feat_getRank;
        internal static MethodInfo Feat_getMaxRankLevel;
        internal static MethodInfo Feat_isLegal;
        internal static FieldInfo Feat_legalPrereqFeat;
        internal static MethodInfo Feat_getPrerequisitFeat;
        internal static MethodInfo AttrSheet_setAttributePoints;
        internal static MethodInfo AttrSheet_setSkillPoints;
        internal static MethodInfo AttrSheet_getAttributePlusObject;
        internal static MethodInfo AttrSheet_getAttributeMinusObject;
        internal static MethodInfo AttrSheet_getSkillPlusObject;
        internal static MethodInfo AttrSheet_getSkillMinusObject;
        internal static MethodInfo CharacterBuilderBase_getCharacter;
        internal static MethodInfo Character_getAttributeRank;
        internal static MethodInfo SkaldBaseObject_getId;

        // ---- Loot-popup grid nav + spell-selector naming + refund cascade
        //      (owner rulings 2026-08-17) ----
        internal static Type PopUpLootType;
        internal static Type PopUpUISystemInventoryType;  // PopUpBase+PopUpUISystemInventory
        internal static Type UIGridInventoryType;
        internal static Type PopUpSpellSelectorType;
        internal static Type ItemType;
        internal static FieldInfo PopUpLoot_inventory;
        internal static FieldInfo PopUpUI_grid;            // PopUpUISystemInventory.grid
        internal static FieldInfo PopUpUIBase_buttons;
        internal static MethodInfo PopUpUIBase_setMouseToClosestButtonAbove;
        internal static MethodInfo PopUpUIBase_setMouseToClosestButtonBelow;
        internal static MethodInfo PopUpUIBase_getControllerScrollableUICanvas;
        internal static MethodInfo PopUpUIBase_setMouseToSelectedButton;
        internal static MethodInfo PopUpBase_updateControllerScrolling;
        internal static MethodInfo UICanvas_incrementCurrentSelectedButton;
        internal static MethodInfo UICanvas_decrementCurrentSelectedButton;
        internal static MethodInfo SkaldObjectList_getObjectList;
        internal static MethodInfo Item_getNameAndAmount;
        internal static MethodInfo PopUpSpellSelector_getLegalSpells;
        internal static MethodInfo Feat_subtractPossibleRank;
        internal static Type AbilitySpellType;
        internal static MethodInfo AbilitySpell_getTier;
        internal static FieldInfo SpellSelector_tierOfSpellsToSelect;
        internal static FieldInfo SpellSelector_spellsSelected;

        // ---- Sheet-grid zones: Grimoire spell grid + Abilities grids
        //      (owner ruling 2026-08-17: A/D crosses rows ↔ grids) ----
        internal static Type SheetClassType;
        internal static Type UISpellBookSheetType;
        internal static Type UIAbilitySheetType;
        internal static Type UIBaseCharacterSheetType;
        internal static Type SceneBaseStateType;   // dialogue-surface family (node re-announce)
        internal static MethodInfo SheetClass_getControllerScrollableList;
        internal static MethodInfo GUIControl_controllerScrollSidewaysLeft;
        internal static MethodInfo GUIControl_controllerScrollSidewaysRight;
        internal static MethodInfo UICanvas_getCurrentSelectedButtonIndex;
        internal static FieldInfo SpellBookSheet_grid;
        internal static FieldInfo SpellBookSheet_spellList;
        internal static FieldInfo SpellList_spells;
        internal static FieldInfo AbilitySheet_gridManeuvers;
        internal static FieldInfo AbilitySheet_gridTriggered;
        internal static FieldInfo AbilitySheet_gridPassive;
        internal static FieldInfo AbilitySheet_maneuverList;
        internal static FieldInfo AbilitySheet_triggeredList;
        internal static FieldInfo AbilitySheet_passiveList;
        internal static FieldInfo BaseCharacterSheet_leftColumn;

        // ---- Character-inventory grid segments (owner ride 2026-08-17:
        //      cells navigated but composed blank; columns moved silently) ----
        internal static Type InventorySegmentType;   // UIInventorySheetBase+UIGridCharacterInventorySegment
        internal static FieldInfo InvSegment_inventory;
        internal static FieldInfo InvSegment_itemTypes;
        internal static FieldInfo InvSegment_gridWidth;
        internal static FieldInfo InvSegment_column;
        internal static FieldInfo InvSheet_secondaryInventoryGrid;
        internal static Type UIInventorySheetCraftingType;
        internal static FieldInfo InvSheetCrafting_listButtons;
        internal static FieldInfo InvSheetCrafting_buttons;
        internal static FieldInfo InvSegment_offsetIndex;
        internal static MethodInfo Inventory_getListByType;
        // The funnel walks the SHEET, not a segment — its index lives there
        // and the cells live on its focus surface (decomp
        // UIInventorySheetBase.cs:586-604; 2026-08-17 fix).
        internal static Type UIInventorySheetBaseType;
        internal static FieldInfo InvSheet_currentControllerSurface;
        internal static FieldInfo InvSheet_mainInventoryGrid;
        // Hover join (owner direction 2026-08-17): the hovered cell is the
        // inventory surface's focus truth.
        internal static FieldInfo InvSegment_grid;
        internal static MethodInfo InvSegment_update;
        internal static MethodInfo UIGridBase_getScrollableElementColumn;
        internal static MethodInfo UIElement_getHover;
        // Filter announce (owner direction 2026-08-17): the Ctrl cycle and the
        // filter-button clicks both land in FilterControl.setFilterByIndex —
        // the one choke for every filter change. Names transcode from the
        // game's own icon paths (the filters render no text).
        internal static Type FilterControlType;      // UIInventorySheetBase+FilterButtons+FilterControl
        internal static MethodInfo FilterControl_setFilterByIndex;
        internal static MethodInfo FilterControl_getFilterIndex;
        internal static MethodInfo FilterControl_getIconPaths;
        // Worn zone (owner direction 2026-08-17): the equipped-items island
        // joins the A/D chain. Cells compose from the same Character getters
        // the renderer paints from (UIInventorySheetBase.cs:232-246).
        internal static Type ItemsWornUIType;        // UIInventorySheetBase+ItemsWornUI
        internal static FieldInfo InvSheet_itemInteractionGrid;
        internal static MethodInfo ItemsWornUI_update;
        internal static FieldInfo ItemsWornUI_grid;
        internal static MethodInfo UIInvSheet_getScrollableElements;
        internal static MethodInfo UIGridBase_getScrollableElementsAtColumn;
        internal static FieldInfo UIGridBase_width;
        internal static MethodInfo[] Character_wornGetters;

        // ---- Mouse guard + attribute-editor flip join (owner rulings
        //      2026-08-17: latch snaps against jitter; speak the flip side) ----
        internal static MethodInfo SkaldIO_updateMousePosition;
        internal static MethodInfo AttributeEditor_scrollSidewaysLeft;
        internal static MethodInfo AttributeEditor_scrollSidewaysRight;
        internal static FieldInfo CharacterSheet_entry1;
        internal static Type EditorSheetEntryType;   // UIBaseCharacterSheet+EditorSheetEntry
        internal static FieldInfo EditorEntry_scrollToPlusButton;

        // ---- Combat spine (CP1, 2026-08-18): turn/round cues, economy diffs,
        //      cost forecast, deployment order. Every name verified against
        //      the decomp this session (combat-survey §4b/§4c cites). Rows
        //      registered here also cover later combat packages (CP2 tactical
        //      choke, CP3 moveCharacter, CP4 AoE census / initiative browser);
        //      Character HP/condition getters land with their consumers, since
        //      an unverified name here would count as a false boot failure. ----
        internal static Type CombatStartStateType;
        internal static Type CombatPlacementStateType;
        internal static Type CombatResolveStateType;
        internal static Type CombatOverStateType;
        internal static Type CombatLogStateType;
        internal static Type CombatContinueType;
        internal static Type CombatTargetingBaseType;
        internal static Type CombatAbilityTargetingType;
        internal static Type CombatSpellTargetingType;
        internal static Type CombatEncounterType;
        internal static Type InitiativeListType;
        internal static Type UIInitiativeListType;
        internal static Type InitiativeButtonType;         // UIInitiativeList+InitiativeButton (non-public nested)
        internal static Type UIActionCounterType;
        internal static Type AbilityUseableType;
        internal static Type HoverElementControlType;
        internal static Type CharacterComponentContainerType;
        internal static Type EffectSelectionType;          // CharacterComponentContainer+EffectSelection
        internal static Type AreaEffectSelectionType;      // CharacterComponentContainer+AreaEffectSelection
        internal static MethodInfo DataControl_getCombatEncounter;
        internal static MethodInfo DataControl_isCombatActive;
        internal static FieldInfo CombatEncounter_turns;           // private short — the round truth
        internal static FieldInfo CombatEncounter_initiativeList;  // private, no accessor
        internal static MethodInfo CombatEncounter_getCurrentCharacter;
        internal static MethodInfo CombatEncounter_getTitle;
        internal static MethodInfo CombatEncounter_getUIInitiativeList;
        internal static MethodInfo CombatEncounter_moveCharacter;  // CP3 consumer
        internal static MethodInfo InitiativeList_getCurrentCharacter;
        internal static MethodInfo InitiativeList_getInitiativeList;
        internal static MethodInfo InitiativeList_printInitiativeOrder;
        internal static MethodInfo InitiativeList_printInitiativeOrderWithRoll;
        internal static MethodInfo InitiativeButton_getCharacter;  // CP4 consumer
        internal static MethodInfo Character_getRemainingCombatMoves;
        internal static MethodInfo Character_getRemainingAttacks;
        internal static MethodInfo Character_getMaxMoves;
        internal static MethodInfo Character_getMaxAttacks;
        internal static MethodInfo Character_isDead;
        internal static FieldInfo Character_moveAlongCombatPath;   // the game's own commit flag
        internal static MethodInfo UIActionCounter_update;         // (Character, AbilityUseable) — forecast note choke
        internal static MethodInfo AbilityUseable_getTimeCost;
        internal static MethodInfo HoverElementControl_addTacticalHoverTextFlashing; // CP2 consumer choke
                                                                   // (NEVER Character.setTacticalHoverText — three-line
                                                                   // wrapper, Mono inline class; survey §4c.3)
        internal static FieldInfo CharacterComponentContainer_areaEffectSelection;   // CP4 consumer (protected)
        internal static MethodInfo Character_getSpellContainer;            // the field's real homes: a Character
        internal static MethodInfo Character_getAbilityManueverContainer;  // HAS two component containers [sic name]
        internal static MethodInfo Map_findCombatPath;             // the game's own hover recompute (mouse-identity)
        internal static MethodInfo Character_getTileParty;
        internal static MethodInfo Character_canCharacterCombatMove;
        internal static MethodInfo Character_isPanicked;
        internal static MethodInfo EffectSelection_getMapTiles;
        internal static MethodInfo EffectSelection_getBaseTile;
        internal static MethodInfo EffectSelection_getAllCharactersInSelection;

        // ---- Combat narration (CP2, 2026-08-18): object-first attribution.
        //      Roster diffs (HP/wounds/death/bark-growth) attribute every log
        //      event to a Character object so the lettered identifiers apply
        //      uniformly (owner mandate — the game's strings carry no such
        //      identifier and are demoted to fact sources, never spoken raw
        //      when a combatant name is present). Tactical flashes are a pure
        //      drain-side READ of the retained element list — no detour, and
        //      the buffer is never consumed (it gates turn pacing, §4c.11). ----
        internal static MethodInfo Character_getVitality;
        internal static MethodInfo Character_getWounds;
        internal static MethodInfo Character_getTargetOpponent;
        internal static MethodInfo Character_isWeaponRanged;
        internal static FieldInfo Character_barkControl;        // the FIELD, never the lazy getter —
                                                                // getBarkControl() force-creates the control,
                                                                // and physicMovementComplete's barkControl!=null
                                                                // clause would then evaluate target-bark waits
                                                                // vanilla skips: observing via the getter would
                                                                // CHANGE combat pacing (review find 1, 2026-08-18)
        internal static Type BarkControlType;
        internal static FieldInfo BarkControl_barks;            // private List<Bark>
        internal static FieldInfo HoverElementControl_tacticalTextList; // private static List<HoverElement>

        // ---- Combat cursor (CP3, 2026-08-18): the overland cursor retargeted.
        //      Anchor = the acting character; path facts from the game's own
        //      hover-recomputed course; placement validity from the game's own
        //      zone compute; disengage/swap forecasts from the game's own
        //      flags (survey §4b.17 — the pips mispaint these; the flags are
        //      the truth). ----
        internal static MethodInfo Character_GetNavigationCourse;   // the hover-preview course (capital G — engine style)
        internal static MethodInfo Character_getMapTile;
        internal static MethodInfo Character_isInMelee;
        internal static MethodInfo Character_getExactRemainingCombatMovesIncludingAttacks;
        internal static FieldInfo Character_dynamicData;
        internal static FieldInfo DynamicData_combatAbilityFlags;   // resolved from the field's own type
        internal static FieldInfo CombatAbilityFlags_evasion;
        internal static FieldInfo CombatAbilityFlags_freeSwap;
        internal static MethodInfo DataControl_getCurrentPC;
        internal static MethodInfo Map_getPreCombatPlacementTiles;
        internal static MethodInfo MapTile_getLightLevel;
        internal static MethodInfo MapTile_getMapObject;            // BaseTemporaryMapObjects (fire)
        internal static Type MapObjectFireType;
        internal static Type BaseTemporaryMapObjectsType;
        internal static MethodInfo TempMapObject_isDead;
        // ---- CP4 (2026-08-18): initiative panel door, Ctrl-row compose,
        //      AoE census, weapon-toggle join ----
        internal static FieldInfo GUIControl_initiativeList;          // the rendered panel (rows rebuilt per frame)
        internal static FieldInfo GUIControl_combatButtonRow;
        internal static MethodInfo GUIControl_setMouseToClosestAbilityButtonAbove;
        internal static MethodInfo GUIControl_setMouseToClosestAbilityButtonBelow;
        internal static FieldInfo CombatPlanning_buttonOptions;       // the row's ButtonData list (hoverText = the label truth)
        internal static MethodInfo Character_printInitiativeStatus;   // Ready/Acted/KO — the game's own words

        internal static MethodInfo NavigationCourse_getDestination;   // stale-course guard (CP3 review find 1)
        internal static MethodInfo Character_isNPCHostile;            // the game's own swap-eligibility test (find 4)
        internal static MethodInfo CombatPlanning_setMousePosition;   // tooltip-clear prefix targets
        internal static MethodInfo CombatPlacement_setMousePosition;
        internal static MethodInfo CombatTargeting_setMousePosition;

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
            GUIControl_getNumericButtonPressIndex = M(typeof(GUIControl), "GUIControl", "getNumericButtonPressIndex");
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

            // Selector grids (WP9)
            UIAbilitySelectorGridType = T("UIAbilitySelectorGrid");
            UICombatSelectorGridType = T("UICombatAbilitySelectorGrid");
            CombatPlanningStateType = T("CombatPlanningState");
            CombatBaseStateType = T("CombatBaseState");
            OverlandStateType = T("OverlandState");
            CharacterType = T("Character");
            ButtonDataType = T("UIButtonControlBase+ButtonData");
            GUIControl_abilitySelectorGrid = F(typeof(GUIControl), "GUIControl", "abilitySelectorGrid");
            GUIControl_setMouseToUIElement = M(typeof(GUIControl), "GUIControl", "setMouseToUIElement");
            GUIControl_sceneDescription = F(typeof(GUIControl), "GUIControl", "sceneDescription");
            UITextBlock_toolTipWords = F(typeof(UITextBlock), "UITextBlock", "toolTipWords");
            GUIControl_setMouseToSelectedOption = M(typeof(GUIControl), "GUIControl", "setMouseToSelectedOption");
            UICanvas_controllerScrollSidewaysLeft = M(UICanvasType, "UICanvas", "controllerScrollSidewaysLeft");
            UICanvas_controllerScrollSidewaysRight = M(UICanvasType, "UICanvas", "controllerScrollSidewaysRight");
            SkaldIO_getOptionSelectionButtonUp = M(SkaldIOType, "SkaldIO", "getOptionSelectionButtonUp");
            SkaldIO_getOptionSelectionButtonDown = M(SkaldIOType, "SkaldIO", "getOptionSelectionButtonDown");
            SkaldIO_getOptionSelectionButtonLeft = M(SkaldIOType, "SkaldIO", "getOptionSelectionButtonLeft");
            SkaldIO_getOptionSelectionButtonRight = M(SkaldIOType, "SkaldIO", "getOptionSelectionButtonRight");
            CombatPlanning_abilityGrid = F(CombatPlanningStateType, "CombatPlanningState", "abilitySelectorGrid");
            CombatPlanning_spellGrid = F(CombatPlanningStateType, "CombatPlanningState", "spellSelectorGrid");
            CombatPlanning_consumableGrid = F(CombatPlanningStateType, "CombatPlanningState", "consumableSelectorGrid");
            Overland_spellGrid = F(OverlandStateType, "OverlandState", "spellSelectorGrid");
            Overland_currentCharacter = F(OverlandStateType, "OverlandState", "currentCharacter");
            CombatBase_getCurrentCharacter = M(CombatBaseStateType, "CombatBaseState", "getCurrentCharacter");
            Character_getCombatAbilityButtonData = M(CharacterType, "Character", "getCombatActivatedAbilityButtonDataList");
            Character_getCombatSpellButtonData = M(CharacterType, "Character", "getCombatActivatedSpellButtonDataList");
            Character_getNonCombatSpellButtonData = M(CharacterType, "Character", "getNonCombatActivatedSpellButtonDataList");
            Character_getInventory = M(CharacterType, "Character", "getInventory");
            // Inventory type from the getter's own signature — no name guess.
            Inventory_getConsumablesButtonData = Character_getInventory == null ? null
                : AccessTools.Method(Character_getInventory.ReturnType, "getConsumablesButtonDataList");
            Row("Inventory.getConsumablesButtonDataList", Inventory_getConsumablesButtonData != null);
            ButtonData_hoverText = F(ButtonDataType, "ButtonData", "hoverText");
            CombatSelectorGrid_textBlock = F(UICombatSelectorGridType, "UICombatAbilitySelectorGrid", "textBlock");
            foreach (string name in MovementReaderNames)
                MovementReaders[name] = M(SkaldIOType, "SkaldIO", name);

            // Overland cursor (WP11)
            MainControlType = T("MainControl");
            DataControlType = T("DataControl");
            MapType = T("Map");
            MapTileType = T("MapTile");
            MapTileGridType = T("MapTileGrid");
            MapIllustratorType = T("MapIllustrator");
            ScrollControlType = T("MapIllustrator+ScrollControl");
            PartyType = T("Party");
            NavigationCourseType = T("NavigationCourse");
            SKALDKeyBindingsType = T("SKALDKeyBindings");
            InventoryType = T("Inventory");
            MainControl_getDataControl = M(MainControlType, "MainControl", "getDataControl");
            DataControl_currentMap = F(DataControlType, "DataControl", "currentMap");
            // TP1: the status strip's one author — recomposed at the drain for
            // provenance identification (pure reads: Calendar/position/weather).
            DataControl_getBuffer = M(DataControlType, "DataControl", "getBuffer");
            Map_getMouseTile = M(MapType, "Map", "getMouseTile");
            Map_isScrollReady = M(MapType, "Map", "isScrollReady");
            Map_getTile = M(MapType, "Map", "getTile", new[] { typeof(int), typeof(int) });
            Map_isTileValid = M(MapType, "Map", "isTileValid", new[] { typeof(int), typeof(int) });
            Map_getXPos = M(MapType, "Map", "getXPos");
            Map_getYPos = M(MapType, "Map", "getYPos");
            Map_getViewportX = M(MapType, "Map", "getViewportX");
            Map_getViewportY = M(MapType, "Map", "getViewportY");
            Map_findPathToMouseTile = M(MapType, "Map", "findPathToMouseTile");
            Map_playerParty = F(MapType, "Map", "playerParty");
            Map_tileGrid = F(MapType, "Map", "tileGrid");
            Map_mapIllustrator = F(MapType, "Map", "mapIllustrator");
            Map_viewDistance = F(MapType, "Map", "viewDistance");
            Map_northernEdgeMapId = F(MapType, "Map", "northernEdgeMapId");
            Map_easternEdgeMapId = F(MapType, "Map", "easternEdgeMapId");
            Map_westernEdgeMapId = F(MapType, "Map", "westernEdgeMapId");
            Map_southernEdgeMapId = F(MapType, "Map", "southernEdgeMapId");
            MapTileGrid_testLineOfSight = M(MapTileGridType, "MapTileGrid", "testLineOfSight",
                new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
            MapIllustrator_scrollControl = F(MapIllustratorType, "MapIllustrator", "scrollControl");
            ScrollControl_scrollX = F(ScrollControlType, "ScrollControl", "scrollX");
            ScrollControl_scrollY = F(ScrollControlType, "ScrollControl", "scrollY");
            MapTile_getLiveCharacter = M(MapTileType, "MapTile", "getLiveCharacter");
            MapTile_getPropOrGuestProp = M(MapTileType, "MapTile", "getPropOrGuestProp");
            MapTile_isSpotted = M(MapTileType, "MapTile", "isSpotted");
            MapTile_isIlluminated = M(MapTileType, "MapTile", "isIlluminated");
            MapTile_isConcealment = M(MapTileType, "MapTile", "isConcealment");
            MapTile_isPassable = M(MapTileType, "MapTile", "isPassable");
            MapTile_isWater = M(MapTileType, "MapTile", "isWater");
            MapTile_isVoidTile = M(MapTileType, "MapTile", "isVoidTile");
            MapTile_getNestedMapId = M(MapTileType, "MapTile", "getNestedMapId");
            MapTile_getInventory = M(MapTileType, "MapTile", "getInventory");
            MapTile_getVehicle = M(MapTileType, "MapTile", "getVehicle");
            MapTile_hasVehicle = M(MapTileType, "MapTile", "hasVehicle");
            MapTile_getDeadParty = M(MapTileType, "MapTile", "getDeadParty");
            MapTile_getInspectDescription = M(MapTileType, "MapTile", "getInspectDescription");
            MapTile_getVerb = M(MapTileType, "MapTile", "getVerb");
            MapTile_getTileX = M(MapTileType, "MapTile", "getTileX");
            MapTile_getTileY = M(MapTileType, "MapTile", "getTileY");
            SkaldBaseObject_getName = M(SkaldBaseObjectType, "SkaldBaseObject", "getName");
            var worldObjectType = T("SkaldWorldObject");
            SkaldWorldObject_getTileX = M(worldObjectType, "SkaldWorldObject", "getTileX");
            SkaldWorldObject_getTileY = M(worldObjectType, "SkaldWorldObject", "getTileY");
            SkaldWorldObject_getContainerMapId = M(worldObjectType, "SkaldWorldObject", "getContainerMapId");
            Inventory_isEmpty = M(InventoryType, "Inventory", "isEmpty");
            Party_getObjectList = M(PartyType, "Party", "getObjectList");
            Party_setNavigationCourse = M(PartyType, "Party", "setNavigationCourse");
            Party_clearNavigationCourse = M(PartyType, "Party", "clearNavigationCourse");
            Party_navigationCourseHasNodes = M(PartyType, "Party", "navigationCourseHasNodes");
            NavigationCourse_getLength = M(NavigationCourseType, "NavigationCourse", "getLength");
            Character_isHostile = M(CharacterType, "Character", "isHostile");
            Character_isPC = M(CharacterType, "Character", "isPC");
            Character_isSpotted = M(CharacterType, "Character", "isSpotted");
            PropType = T("Prop");
            Prop_isHidden = M(PropType, "Prop", "isHidden");
            Prop_shouldNotBeDrawn = M(PropType, "Prop", "shouldNotBeDrawn");
            Prop_shouldBeRemovedFromGame = M(PropType, "Prop", "shouldBeRemovedFromGame");
            PropLockable_isLocked = M(T("PropLockable"), "PropLockable", "isLocked");
            SkaldIO_setVirtualMousePosition = M(SkaldIOType, "SkaldIO", "setVirtualMousePosition",
                new[] { typeof(int), typeof(int) });
            SkaldIO_updateMousePosition = M(SkaldIOType, "SkaldIO", "updateMousePosition");
            AttributeEditor_scrollSidewaysLeft = M(T("UIAttributeEditorSheet"), "UIAttributeEditorSheet", "controllerScrollSidewaysLeft");
            AttributeEditor_scrollSidewaysRight = M(T("UIAttributeEditorSheet"), "UIAttributeEditorSheet", "controllerScrollSidewaysRight");
            var baseCharacterSheet = T("UIBaseCharacterSheet");
            CharacterSheet_entry1 = F(baseCharacterSheet, "UIBaseCharacterSheet", "entry1");
            EditorSheetEntryType = baseCharacterSheet?.GetNestedType("EditorSheetEntry",
                BindingFlags.NonPublic | BindingFlags.Public);
            Row("UIBaseCharacterSheet+EditorSheetEntry", EditorSheetEntryType != null);
            EditorEntry_scrollToPlusButton = F(EditorSheetEntryType, "EditorSheetEntry", "controllerScrollToPlusButton");
            ToolTipPrinter_clearToolTip = M(ToolTipPrinterType, "ToolTipPrinter", "clearToolTip");
            ToolTipPrinter_hasToolTip = M(ToolTipPrinterType, "ToolTipPrinter", "hasToolTip");
            OverlandState_setMouseInput = M(OverlandStateType, "OverlandState", "setMouseInput");
            KeyBindings_getUpAltKey = M(SKALDKeyBindingsType, "SKALDKeyBindings", "getUpAltKey");
            KeyBindings_getDownAltKey = M(SKALDKeyBindingsType, "SKALDKeyBindings", "getDownAltKey");
            KeyBindings_getLeftAltKey = M(SKALDKeyBindingsType, "SKALDKeyBindings", "getLeftAltKey");
            KeyBindings_getRightAltKey = M(SKALDKeyBindingsType, "SKALDKeyBindings", "getRightAltKey");
            PropDoorType = T("PropDoor");
            PropContType = T("PropCont");
            PropWarpType = T("PropWarp");
            PropPickupType = T("PropPickup");
            PropDecorativeType = T("PropDecorative");
            PropBeaconType = T("PropBeacon");
            PropSpawnerType = T("PropSpawner");
            PropTriggerType = T("PropTrigger");

            // Character creation: point pools + feat tree (CC report build)
            UIFeatTreeType = T("UIFeatTree");
            FeatTreeCollectionType = T("UIFeatTree+FeatTreeCollection");
            FeatTreeType = T("UIFeatTree+FeatTreeCollection+FeatTree");
            UIAttributeEditorSheetType = T("UIAttributeEditorSheet");
            CharacterBuilderBaseStateType = T("CharacterBuilderBaseState");
            UIFeatTree_setPointsText = M(UIFeatTreeType, "UIFeatTree", "setPointsText");
            UIFeatTree_treeCollection = F(UIFeatTreeType, "UIFeatTree", "treeCollection");
            UIFeatTree_updatePressedLeftFeat = M(UIFeatTreeType, "UIFeatTree", "updatePressedLeftFeat");
            UIFeatTree_updatePressedRightFeat = M(UIFeatTreeType, "UIFeatTree", "updatePressedRightFeat");
            UIFeatTree_pressedLeftFeat = F(UIFeatTreeType, "UIFeatTree", "pressedLeftFeat");
            UIFeatTree_pressedRightFeat = F(UIFeatTreeType, "UIFeatTree", "pressedRightFeat");
            FeatTreeCollection_controllerScrollIndex = F(FeatTreeCollectionType, "FeatTreeCollection", "controllerScrollIndex");
            FeatTree_nodeDictionary = F(FeatTreeType, "FeatTree", "nodeDictionary");
            Feat_getRank = M(FeatType, "Feat", "getRank");
            Feat_getMaxRankLevel = M(FeatType, "Feat", "getMaxRankLevel");
            Feat_isLegal = M(FeatType, "Feat", "isLegal");
            Feat_legalPrereqFeat = F(FeatType, "Feat", "legalPrereqFeat");
            Feat_getPrerequisitFeat = M(FeatType, "Feat", "getPrerequisitFeat");
            AttrSheet_setAttributePoints = M(UIAttributeEditorSheetType, "UIAttributeEditorSheet", "setAttributePoints");
            AttrSheet_setSkillPoints = M(UIAttributeEditorSheetType, "UIAttributeEditorSheet", "setSkillPoints");
            AttrSheet_getAttributePlusObject = M(UIAttributeEditorSheetType, "UIAttributeEditorSheet", "getAttributePlusObject");
            AttrSheet_getAttributeMinusObject = M(UIAttributeEditorSheetType, "UIAttributeEditorSheet", "getAttributeMinusObject");
            AttrSheet_getSkillPlusObject = M(UIAttributeEditorSheetType, "UIAttributeEditorSheet", "getSkillPlusObject");
            AttrSheet_getSkillMinusObject = M(UIAttributeEditorSheetType, "UIAttributeEditorSheet", "getSkillMinusObject");
            CharacterBuilderBase_getCharacter = M(CharacterBuilderBaseStateType, "CharacterBuilderBaseState", "getCharacter");
            Character_getAttributeRank = M(CharacterType, "Character", "getAttributeRank", new[] { typeof(string) });
            SkaldBaseObject_getId = M(SkaldBaseObjectType, "SkaldBaseObject", "getId");

            // Loot-popup grid nav + spell-selector naming + refund cascade
            PopUpLootType = T("PopUpLoot");
            PopUpUISystemInventoryType = T("PopUpBase+PopUpUISystemInventory");
            UIGridInventoryType = T("UIGridInventory");
            PopUpSpellSelectorType = T("PopUpSpellSelector");
            ItemType = T("Item");
            PopUpLoot_inventory = F(PopUpLootType, "PopUpLoot", "inventory");
            PopUpUI_grid = F(PopUpUISystemInventoryType, "PopUpUISystemInventory", "grid");
            PopUpUIBase_buttons = F(PopUpUIBaseType, "PopUpUIBase", "buttons");
            PopUpUIBase_setMouseToClosestButtonAbove = M(PopUpUIBaseType, "PopUpUIBase", "setMouseToClosestButtonAbove");
            PopUpUIBase_setMouseToClosestButtonBelow = M(PopUpUIBaseType, "PopUpUIBase", "setMouseToClosestButtonBelow");
            PopUpUIBase_getControllerScrollableUICanvas = M(PopUpUIBaseType, "PopUpUIBase", "getControllerScrollableUICanvas");
            PopUpUIBase_setMouseToSelectedButton = M(PopUpUIBaseType, "PopUpUIBase", "setMouseToSelectedButton");
            PopUpBase_updateControllerScrolling = M(PopUpBaseType, "PopUpBase", "updateControllerScrolling");
            UICanvas_incrementCurrentSelectedButton = M(UICanvasType, "UICanvas", "incrementCurrentSelectedButton");
            UICanvas_decrementCurrentSelectedButton = M(UICanvasType, "UICanvas", "decrementCurrentSelectedButton");
            SkaldObjectList_getObjectList = M(SkaldObjectListType, "SkaldObjectList", "getObjectList");
            Item_getNameAndAmount = M(ItemType, "Item", "getNameAndAmount");
            PopUpSpellSelector_getLegalSpells = M(PopUpSpellSelectorType, "PopUpSpellSelector", "getLegalSpells");
            Feat_subtractPossibleRank = M(FeatType, "Feat", "subtractPossibleRank");
            AbilitySpellType = T("AbilitySpell");
            AbilitySpell_getTier = M(AbilitySpellType, "AbilitySpell", "getTier");
            SpellSelector_tierOfSpellsToSelect = F(PopUpSpellSelectorType, "PopUpSpellSelector", "tierOfSpellsToSelect");
            SpellSelector_spellsSelected = F(PopUpSpellSelectorType, "PopUpSpellSelector", "spellsSelected");

            // Sheet-grid zones (Grimoire / Abilities)
            SheetClassType = T("SheetClass");
            UISpellBookSheetType = T("UISpellBookSheet");
            UIAbilitySheetType = T("UIAbilitySheet");
            UIBaseCharacterSheetType = T("UIBaseCharacterSheet");
            SceneBaseStateType = T("SceneBaseState");
            SheetClass_getControllerScrollableList = M(SheetClassType, "SheetClass", "getControllerScrollableList");
            GUIControl_controllerScrollSidewaysLeft = M(typeof(GUIControl), "GUIControl", "controllerScrollSidewaysLeft");
            GUIControl_controllerScrollSidewaysRight = M(typeof(GUIControl), "GUIControl", "controllerScrollSidewaysRight");
            UICanvas_getCurrentSelectedButtonIndex = M(UICanvasType, "UICanvas", "getCurrentSelectedButtonIndex");
            SpellBookSheet_grid = F(UISpellBookSheetType, "UISpellBookSheet", "grid");
            SpellBookSheet_spellList = F(UISpellBookSheetType, "UISpellBookSheet", "spellList");
            SpellList_spells = F(T("SpellContainer+SpellList"), "SpellContainer.SpellList", "spells");
            AbilitySheet_gridManeuvers = F(UIAbilitySheetType, "UIAbilitySheet", "gridManeuvers");
            AbilitySheet_gridTriggered = F(UIAbilitySheetType, "UIAbilitySheet", "gridTriggered");
            AbilitySheet_gridPassive = F(UIAbilitySheetType, "UIAbilitySheet", "gridPassive");
            AbilitySheet_maneuverList = F(UIAbilitySheetType, "UIAbilitySheet", "maneuverList");
            AbilitySheet_triggeredList = F(UIAbilitySheetType, "UIAbilitySheet", "triggeredAbilityList");
            AbilitySheet_passiveList = F(UIAbilitySheetType, "UIAbilitySheet", "passiveAbilityList");
            BaseCharacterSheet_leftColumn = F(UIBaseCharacterSheetType, "UIBaseCharacterSheet", "leftColumn");

            // Character-inventory grid segments
            InventorySegmentType = T("UIInventorySheetBase+UIGridCharacterInventorySegment");
            InvSegment_inventory = F(InventorySegmentType, "UIGridCharacterInventorySegment", "inventory");
            InvSegment_itemTypes = F(InventorySegmentType, "UIGridCharacterInventorySegment", "itemTypes");
            InvSegment_gridWidth = F(InventorySegmentType, "UIGridCharacterInventorySegment", "gridWidth");
            InvSegment_column = F(InventorySegmentType, "UIGridCharacterInventorySegment", "controllerSelectColumn");
            InvSegment_offsetIndex = F(InventorySegmentType, "UIGridCharacterInventorySegment", "offsetIndex");
            Inventory_getListByType = M(InventoryType, "Inventory", "getListByType");
            UIInventorySheetBaseType = T("UIInventorySheetBase");
            InvSheet_currentControllerSurface = F(UIInventorySheetBaseType, "UIInventorySheetBase", "currentControllerSurface");
            InvSheet_secondaryInventoryGrid = F(UIInventorySheetBaseType, "UIInventorySheetBase", "secondaryInventoryGrid");
            // Crafting zone chain (owner build 2026-08-18): the crafting
            // sheet's two mouse islands — the workstation grid and the
            // Craft/Clear technical row (AXBY-no-numbers: mouse or controller
            // A/X only in the shipped game).
            UIInventorySheetCraftingType = T("UIInventorySheetCrafting");
            InvSheetCrafting_listButtons = F(UIInventorySheetCraftingType, "UIInventorySheetCrafting", "listButtons");
            InvSheetCrafting_buttons = F(UIInventorySheetCraftingType, "UIInventorySheetCrafting", "buttons");
            InvSheet_mainInventoryGrid = F(UIInventorySheetBaseType, "UIInventorySheetBase", "mainInventoryGrid");
            InvSegment_grid = F(InventorySegmentType, "UIGridCharacterInventorySegment", "grid");
            InvSegment_update = M(InventorySegmentType, "UIGridCharacterInventorySegment", "update");
            var uiGridBase = T("UIGridBase");
            UIGridBase_getScrollableElementColumn = M(uiGridBase, "UIGridBase", "getScrollableElementColumn");
            UIGridBase_getScrollableElementsAtColumn = M(uiGridBase, "UIGridBase", "getScrollableElements", new[] { typeof(int) });
            UIGridBase_width = F(uiGridBase, "UIGridBase", "width");

            // Filter announce + worn zone (2026-08-17)
            FilterControlType = T("UIInventorySheetBase+FilterButtons+FilterControl");
            FilterControl_setFilterByIndex = M(FilterControlType, "FilterControl", "setFilterByIndex", new[] { typeof(int) });
            FilterControl_getFilterIndex = M(FilterControlType, "FilterControl", "getFilterIndex");
            FilterControl_getIconPaths = M(FilterControlType, "FilterControl", "getIconPaths");
            ItemsWornUIType = T("UIInventorySheetBase+ItemsWornUI");
            InvSheet_itemInteractionGrid = F(UIInventorySheetBaseType, "UIInventorySheetBase", "itemInteractionGrid");
            ItemsWornUI_update = M(ItemsWornUIType, "ItemsWornUI", "update");
            ItemsWornUI_grid = F(ItemsWornUIType, "ItemsWornUI", "grid");
            UIInvSheet_getScrollableElements = M(UIInventorySheetBaseType, "UIInventorySheetBase", "getScrollableElements", Type.EmptyTypes);
            // Worn slots in the renderer's own setButtons order
            // (UIInventorySheetBase.cs:232-246): row 0 then row 1, left to right.
            string[] wornGetterNames =
            {
                "getCurrentMeleeWeapon", "getCurrentRangedWeapon", "getCurrentArmor",
                "getCurrentShieldRegardlessIfWorn", "getCurrentAmmo", "getCurrentRing",
                "getCurrentHeadwear", "getCurrentClothing", "getCurrentGloves",
                "getCurrentFootwear", "getCurrentLight", "getCurrentNecklace"
            };
            Character_wornGetters = new MethodInfo[wornGetterNames.Length];
            for (int i = 0; i < wornGetterNames.Length; i++)
                Character_wornGetters[i] = M(CharacterType, "Character", wornGetterNames[i]);
            UIElement_getHover = M(T("UIElement"), "UIElement", "getHover");

            // Combat spine (CP1)
            CombatStartStateType = T("CombatStartState");
            CombatPlacementStateType = T("CombatPlacementState");
            CombatResolveStateType = T("CombatResolveState");
            CombatOverStateType = T("CombatOverState");
            CombatLogStateType = T("CombatLogState");
            CombatContinueType = T("CombatContinue");
            CombatTargetingBaseType = T("CombatTargetingBase");
            CombatAbilityTargetingType = T("CombatAbilityTargeting");
            CombatSpellTargetingType = T("CombatSpellTargeting");
            CombatEncounterType = T("CombatEncounter");
            InitiativeListType = T("InitiativeList");
            UIInitiativeListType = T("UIInitiativeList");
            InitiativeButtonType = UIInitiativeListType?.GetNestedType("InitiativeButton",
                BindingFlags.NonPublic | BindingFlags.Public);
            Row("UIInitiativeList+InitiativeButton", InitiativeButtonType != null);
            UIActionCounterType = T("UIActionCounter");
            AbilityUseableType = T("AbilityUseable");
            HoverElementControlType = T("HoverElementControl");
            CharacterComponentContainerType = T("CharacterComponentContainer");
            EffectSelectionType = T("CharacterComponentContainer+EffectSelection");
            AreaEffectSelectionType = T("CharacterComponentContainer+AreaEffectSelection");
            DataControl_getCombatEncounter = M(DataControlType, "DataControl", "getCombatEncounter");
            DataControl_isCombatActive = M(DataControlType, "DataControl", "isCombatActive");
            CombatEncounter_turns = F(CombatEncounterType, "CombatEncounter", "turns");
            CombatEncounter_initiativeList = F(CombatEncounterType, "CombatEncounter", "initiativeList");
            CombatEncounter_getCurrentCharacter = M(CombatEncounterType, "CombatEncounter", "getCurrentCharacter");
            CombatEncounter_getTitle = M(CombatEncounterType, "CombatEncounter", "getTitle");
            CombatEncounter_getUIInitiativeList = M(CombatEncounterType, "CombatEncounter", "getUIInitiativeList");
            CombatEncounter_moveCharacter = M(CombatEncounterType, "CombatEncounter", "moveCharacter");
            InitiativeList_getCurrentCharacter = M(InitiativeListType, "InitiativeList", "getCurrentCharacter");
            InitiativeList_getInitiativeList = M(InitiativeListType, "InitiativeList", "getInitiativeList");
            InitiativeList_printInitiativeOrder = M(InitiativeListType, "InitiativeList", "printInitiativeOrder");
            InitiativeList_printInitiativeOrderWithRoll = M(InitiativeListType, "InitiativeList", "printInitiativeOrderWithRoll");
            InitiativeButton_getCharacter = M(InitiativeButtonType, "InitiativeButton", "getCharacter");
            Character_getRemainingCombatMoves = M(CharacterType, "Character", "getRemainingCombatMoves");
            Character_getRemainingAttacks = M(CharacterType, "Character", "getRemainingAttacks");
            Character_getMaxMoves = M(CharacterType, "Character", "getMaxMoves");
            Character_getMaxAttacks = M(CharacterType, "Character", "getMaxAttacks");
            Character_isDead = M(CharacterType, "Character", "isDead");
            Character_moveAlongCombatPath = F(CharacterType, "Character", "moveAlongCombatPath");
            UIActionCounter_update = M(UIActionCounterType, "UIActionCounter", "update");
            AbilityUseable_getTimeCost = M(AbilityUseableType, "AbilityUseable", "getTimeCost");
            HoverElementControl_addTacticalHoverTextFlashing
                = M(HoverElementControlType, "HoverElementControl", "addTacticalHoverTextFlashing");
            CharacterComponentContainer_areaEffectSelection
                = F(CharacterComponentContainerType, "CharacterComponentContainer", "areaEffectSelection");
            Character_getSpellContainer = M(CharacterType, "Character", "getSpellContainer");
            Character_getAbilityManueverContainer = M(CharacterType, "Character", "getAbilityManueverContainer");
            Map_findCombatPath = M(MapType, "Map", "findCombatPath");
            Character_getTileParty = M(CharacterType, "Character", "getTileParty");
            Character_canCharacterCombatMove = M(CharacterType, "Character", "canCharacterCombatMove");
            Character_isPanicked = M(CharacterType, "Character", "isPanicked");
            EffectSelection_getMapTiles = M(EffectSelectionType, "EffectSelection", "getMapTiles");
            EffectSelection_getBaseTile = M(EffectSelectionType, "EffectSelection", "getBaseTile");
            EffectSelection_getAllCharactersInSelection
                = M(EffectSelectionType, "EffectSelection", "getAllCharactersInSelection");

            // Combat narration (CP2)
            Character_getVitality = M(CharacterType, "Character", "getVitality");
            Character_getWounds = M(CharacterType, "Character", "getWounds");
            Character_getTargetOpponent = M(CharacterType, "Character", "getTargetOpponent");
            Character_isWeaponRanged = M(CharacterType, "Character", "isWeaponRanged");
            Character_barkControl = F(CharacterType, "Character", "barkControl");
            BarkControlType = T("BarkControl");
            BarkControl_barks = F(BarkControlType, "BarkControl", "barks");
            HoverElementControl_tacticalTextList
                = F(HoverElementControlType, "HoverElementControl", "tacticalTextList");

            // Combat cursor (CP3)
            Character_GetNavigationCourse = M(CharacterType, "Character", "GetNavigationCourse");
            Character_getMapTile = M(CharacterType, "Character", "getMapTile");
            Character_isInMelee = M(CharacterType, "Character", "isInMelee");
            Character_getExactRemainingCombatMovesIncludingAttacks
                = M(CharacterType, "Character", "getExactRemainingCombatMovesIncludingAttacks");
            Character_dynamicData = F(CharacterType, "Character", "dynamicData");
            DynamicData_combatAbilityFlags = Character_dynamicData == null ? null
                : AccessTools.Field(Character_dynamicData.FieldType, "combatAbilityFlags");
            Row("DynamicData.combatAbilityFlags", DynamicData_combatAbilityFlags != null);
            CombatAbilityFlags_evasion = DynamicData_combatAbilityFlags == null ? null
                : AccessTools.Field(DynamicData_combatAbilityFlags.FieldType, "evasion");
            Row("CombatAbilityFlags.evasion", CombatAbilityFlags_evasion != null);
            CombatAbilityFlags_freeSwap = DynamicData_combatAbilityFlags == null ? null
                : AccessTools.Field(DynamicData_combatAbilityFlags.FieldType, "freeSwap");
            Row("CombatAbilityFlags.freeSwap", CombatAbilityFlags_freeSwap != null);
            DataControl_getCurrentPC = M(DataControlType, "DataControl", "getCurrentPC");
            Map_getPreCombatPlacementTiles = M(MapType, "Map", "getPreCombatPlacementTiles");
            MapTile_getLightLevel = M(MapTileType, "MapTile", "getLightLevel");
            MapTile_getMapObject = M(MapTileType, "MapTile", "getMapObject");
            MapObjectFireType = T("MapObjectFire");
            BaseTemporaryMapObjectsType = T("BaseTemporaryMapObjects");
            TempMapObject_isDead = M(BaseTemporaryMapObjectsType, "BaseTemporaryMapObjects", "isDead");
            GUIControl_initiativeList = F(typeof(GUIControl), "GUIControl", "initiativeList");
            GUIControl_combatButtonRow = F(typeof(GUIControl), "GUIControl", "combatButtonRow");
            GUIControl_setMouseToClosestAbilityButtonAbove
                = M(typeof(GUIControl), "GUIControl", "setMouseToClosestAbilityButtonAbove");
            GUIControl_setMouseToClosestAbilityButtonBelow
                = M(typeof(GUIControl), "GUIControl", "setMouseToClosestAbilityButtonBelow");
            CombatPlanning_buttonOptions = F(CombatPlanningStateType, "CombatPlanningState", "buttonOptions");
            Character_printInitiativeStatus = M(CharacterType, "Character", "printInitiativeStatus");
            NavigationCourse_getDestination = M(NavigationCourseType, "NavigationCourse", "getDestination");
            Character_isNPCHostile = M(CharacterType, "Character", "isNPCHostile", new[] { CharacterType });
            CombatPlanning_setMousePosition = M(CombatPlanningStateType, "CombatPlanningState", "setMousePosition");
            CombatPlacement_setMousePosition = M(CombatPlacementStateType, "CombatPlacementState", "setMousePosition");
            CombatTargeting_setMousePosition = M(CombatTargetingBaseType, "CombatTargetingBase", "setMousePosition");

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

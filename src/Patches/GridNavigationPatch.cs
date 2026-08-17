using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// WP9: selector-grid wiring (owner-approved spec, build-plan §6). The
    /// game's UIAbilitySelectorGrid implements full 2D scrollable-element
    /// navigation that no state ever drives — combat's ability/spell/consumable
    /// grids and the overland spell grid are hover/click-only even on
    /// controller. Port-first: the game built the cursor; the mod supplies the
    /// two missing calls.
    ///
    /// 1. The wire (GridWirePatch): while GUIControl.abilitySelectorGrid is
    ///    set, getControllerScrollableList returns it — grid-open is modal
    ///    (owner ruling 2026-08-16; precedent: the game's own Esc handler
    ///    clears grids before opening the menu). Downstream is native: the
    ///    funnel steps the index and the virtual-mouse snap makes the game's
    ///    own hover pipeline voice the entry (descBuffer →
    ///    setSecondaryDescription); Z = native LT-click activates; X =
    ///    right-click tooltip.
    ///
    /// 2. The driver (Tick, from InputHandler): the four-line BaseMenuState
    ///    idiom — option-selection accessors → setMouseToClosestOptionAbove/
    ///    Below and the grid's own controllerScrollSideways*. Combat/overland
    ///    GUIControls never set snapToOptions, so the native post-move snap
    ///    no-ops there — the driver snaps explicitly with the popup grid
    ///    family's own offset (PopUpUIBase.setMouseToSelectedButton: +8,-8).
    ///    On arrival the first cell is focused through the game's own
    ///    setCurrentSelectedButton (the selection join speaks it — entry-focus
    ///    ruling); an empty grid speaks its own rendered "-Empty-" block.
    ///
    /// 3. Movement suppression (MovementSuppressPatch): combat planning moves
    ///    the character on getPressedUp/Down/Left/RightKey and overland on
    ///    getDownUp/Down/Left/RightKey, both ungated when a grid is open — the
    ///    eight accessors read false while a grid is live. The grid flag is
    ///    the game's own field, read at time of use, never a mod mirror.
    /// </summary>
    public static class GridNavigationPatch
    {
        // ---- Live game-truth reads (memoized once per frame) ----
        private static int _frame = -1;
        private static object _gui, _grid;
        private static object _lastGrid;

        private static void Refresh()
        {
            if (Time.frameCount == _frame) return;
            _frame = Time.frameCount;
            _gui = null;
            _grid = null;
            try
            {
                if (Seams.StateBase_guiControl == null || Seams.GUIControl_abilitySelectorGrid == null) return;
                object state = Pump.CurrentStateObject();
                if (state == null) return;
                _gui = Seams.StateBase_guiControl.GetValue(state);
                if (_gui != null) _grid = Seams.GUIControl_abilitySelectorGrid.GetValue(_gui);
            }
            catch { }
        }

        /// <summary>The game's own active-selector-grid flag, at time of use.</summary>
        public static bool GridActive()
        {
            Refresh();
            return _grid != null;
        }

        // ---- (1) The wire ----

        [HarmonyPatch]
        public static class GridWirePatch
        {
            [HarmonyPrepare]
            static bool Prepare()
            {
                if (Seams.GUIControl_getControllerScrollableList == null
                    || Seams.GUIControl_abilitySelectorGrid == null)
                {
                    Plugin.Logger?.LogWarning("[GridNav] wire seams missing — selector grids stay mouse-only");
                    return false;
                }
                return true;
            }

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.GUIControl_getControllerScrollableList;

            [HarmonyPrefix]
            static bool Prefix(object __instance, ref UICanvas __result)
            {
                try
                {
                    if (Seams.GUIControl_abilitySelectorGrid.GetValue(__instance) is UICanvas grid)
                    {
                        __result = grid;
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }

        // ---- (3) Movement suppression while a grid is open ----
        // NOT an attribute class: these are SkaldIO methods, and every SkaldIO
        // patch must apply in the DEFERRED batch (SkaldIOPatches.ApplyPatches,
        // past first state classification) — detouring SkaldIO at frame 0
        // forces its static constructor on unloaded game data and poisons the
        // type (the WP7 boot-kill; re-learned the hard way 2026-08-16 when an
        // Awake-time version of this patch froze the boot at frame 12).

        /// <summary>Applied from SkaldIOPatches.ApplyPatches, one accessor at
        /// a time so a single bad target costs one reader, not the batch.</summary>
        internal static void ApplyMovementSuppression(Harmony harmony)
        {
            int applied = 0;
            foreach (var kv in Seams.MovementReaders)
            {
                if (kv.Value == null) continue;
                try
                {
                    harmony.Patch(kv.Value, postfix: new HarmonyMethod(
                        typeof(GridNavigationPatch), nameof(Postfix_SuppressMovement)));
                    applied++;
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogError($"[GridNav] Movement suppression failed on {kv.Key}: {ex.Message}");
                }
            }
            if (applied == Seams.MovementReaderNames.Length)
                Plugin.Logger?.LogInfo($"[GridNav] Grid-modal movement suppression armed ({applied}/{Seams.MovementReaderNames.Length})");
            else
                Plugin.Logger?.LogError($"[GridNav] Movement suppression incomplete ({applied}/{Seams.MovementReaderNames.Length}) — grid-open may leak moves");
        }

        static void Postfix_SuppressMovement(ref bool __result)
        {
            if (__result && GridActive()) __result = false;
        }

        // ---- (2) The driver ----

        /// <summary>Called from InputHandler each frame (after the text-entry
        /// gate and the review layer).</summary>
        public static void Tick()
        {
            try
            {
                Refresh();
                if (_grid == null)
                {
                    _lastGrid = null;
                    return;
                }

                if (!ReferenceEquals(_grid, _lastGrid))
                {
                    _lastGrid = _grid;
                    Arrive();
                    return;
                }

                if (Pressed(Seams.SkaldIO_getOptionSelectionButtonUp))
                {
                    Seams.GUIControl_setMouseToClosestOptionAbove?.Invoke(_gui, null);
                    Snap();
                }
                else if (Pressed(Seams.SkaldIO_getOptionSelectionButtonDown))
                {
                    Seams.GUIControl_setMouseToClosestOptionBelow?.Invoke(_gui, null);
                    Snap();
                }
                else if (Pressed(Seams.SkaldIO_getOptionSelectionButtonLeft))
                {
                    Seams.UICanvas_controllerScrollSidewaysLeft?.Invoke(_grid, null);
                    Snap();
                }
                else if (Pressed(Seams.SkaldIO_getOptionSelectionButtonRight))
                {
                    Seams.UICanvas_controllerScrollSidewaysRight?.Invoke(_grid, null);
                    Snap();
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[GridNav] {ex.Message}");
            }
        }

        private static bool Pressed(MethodInfo accessor)
            => accessor != null && (bool)accessor.Invoke(null, null);

        /// <summary>Explicit virtual-mouse snap to the focused cell — the
        /// native setMouseToSelectedOption is gated on snapToOptions, which
        /// combat/overland never set.</summary>
        private static void Snap()
        {
            try { Seams.GUIControl_setMouseToUIElement?.Invoke(_gui, new object[] { _grid, 8, -8 }); }
            catch { }
        }

        /// <summary>Grid arrival: focus the first cell through the game's own
        /// setter (the selection join voices it), or speak the grid's rendered
        /// empty text when there is nothing to focus.</summary>
        private static void Arrive()
        {
            var elements = Seams.UICanvas_getScrollableElements?.Invoke(_grid, null) as ICollection;
            int count = elements?.Count ?? 0;
            if (count == 0)
            {
                Scaffold.SpeechService.Say(EmptyText() ?? "Empty.", "Nav");
                return;
            }
            Seams.UICanvas_setCurrentSelectedButton?.Invoke(_grid, new object[] { 0 });
            Snap();
        }

        private static string EmptyText()
        {
            try
            {
                if (Seams.CombatSelectorGrid_textBlock == null || Seams.UITextBlock_content == null) return null;
                if (Seams.UICombatSelectorGridType == null || !Seams.UICombatSelectorGridType.IsInstanceOfType(_grid)) return null;
                object block = Seams.CombatSelectorGrid_textBlock.GetValue(_grid);
                string raw = block != null ? Seams.UITextBlock_content.GetValue(block) as string : null;
                string cleaned = string.IsNullOrWhiteSpace(raw) ? null : TextCleaner.CleanText(raw);
                return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
            }
            catch { return null; }
        }

        // ---- Drain-side name resolution (Pump composition) ----

        /// <summary>The focused cell's name from the game's own button-data
        /// list — the exact list the grid was built from, index-aligned by
        /// construction (ButtonData.hoverText = the component's getName()).
        /// Read-only; null when the state/grid pairing is unknown.</summary>
        internal static string NameAt(object grid, int index)
        {
            try
            {
                if (grid == null || Seams.ButtonData_hoverText == null) return null;
                object state = Pump.CurrentStateObject();
                if (state == null) return null;

                MethodInfo listGetter = null;
                object listOwner = null;

                if (Seams.CombatPlanningStateType != null && Seams.CombatPlanningStateType.IsInstanceOfType(state))
                {
                    object character = Seams.CombatBase_getCurrentCharacter?.Invoke(state, null);
                    if (character == null) return null;
                    if (IsGridField(Seams.CombatPlanning_abilityGrid, state, grid))
                    {
                        listGetter = Seams.Character_getCombatAbilityButtonData;
                        listOwner = character;
                    }
                    else if (IsGridField(Seams.CombatPlanning_spellGrid, state, grid))
                    {
                        listGetter = Seams.Character_getCombatSpellButtonData;
                        listOwner = character;
                    }
                    else if (IsGridField(Seams.CombatPlanning_consumableGrid, state, grid))
                    {
                        listGetter = Seams.Inventory_getConsumablesButtonData;
                        listOwner = Seams.Character_getInventory?.Invoke(character, null);
                    }
                }
                else if (Seams.OverlandStateType != null && Seams.OverlandStateType.IsInstanceOfType(state))
                {
                    if (IsGridField(Seams.Overland_spellGrid, state, grid))
                    {
                        listGetter = Seams.Character_getNonCombatSpellButtonData;
                        listOwner = Seams.Overland_currentCharacter?.GetValue(state);
                    }
                }

                if (listGetter == null || listOwner == null) return null;
                var list = listGetter.Invoke(listOwner, null) as IList;
                if (list == null || index < 0 || index >= list.Count) return null;
                return Seams.ButtonData_hoverText.GetValue(list[index]) as string;
            }
            catch { return null; }
        }

        private static bool IsGridField(FieldInfo field, object state, object grid)
            => field != null && ReferenceEquals(field.GetValue(state), grid);
    }
}

using HarmonyLib;
using System;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Forced controller mode (build-plan WP7, owner ruling 2026-08-16 —
    /// supersedes the March gate-removal layer, whose option-funnel arrow
    /// OR-ins, Enter/RightShift click synthesis, re-implemented mouse-snap
    /// methods, index-math increment/decrement prefixes, and scrollbar
    /// suppression are all deleted; the native controller paths now run).
    ///
    /// 1. SkaldIO.isControllerConnected → true. Every native gate site flips on
    ///    (option funnel, popup scrolling, virtual-mouse snapping, D-pad ability
    ///    row, single-click grid semantics) — the Steam Deck hard-wires this
    ///    exact mode, so it is a shipped first-class configuration. Keyboard
    ///    reaches the controller layer via ControllerFeedPatch.
    ///
    /// 2. Edge clamp (bug-ledger B4, ruled 2026-08-16): at a true list edge the
    ///    native setMouseToClosestOptionAbove/Below routes the press to a pane
    ///    scroll (GUIControl.cs:1791-1821) — focus doesn't move and nothing
    ///    speaks. canControllerScrollUp/Down test the full element list
    ///    (UICanvas.cs:71-79), so every element is reachable without that
    ///    branch; the prefix suppresses it at the edge and notes
    ///    "Top of list." / "Bottom of list." (clamp, no wrap — game logic).
    ///    Screens where scrolling IS the navigation (GUIControlCredits)
    ///    override these methods without calling base and are untouched.
    ///
    /// 3. SheetComplexSettings.getControllerScrollableList null-fallback (kept):
    ///    the keybindings screen has no sliderControl and natively returns null,
    ///    dead-ending controller navigation there too — listButtons is the
    ///    game's own structure, wired in.
    ///
    /// Applied from Plugin.Update after the first state classification —
    /// SkaldIO's static constructor needs game data (C64Color) loaded.
    /// </summary>
    public static class SkaldIOPatches
    {
        private static bool _applied;

        // Bridge drive (dev-only; owner-sanctioned 2026-08-16): SkaldBridge arms
        // these by reflection — class and field names are the bridge's API.
        // Consumed by ControllerFeedPatch's OR-ins (up/down/left/right → left
        // stick, confirm → LT click, cancel → B), same path as real keys.
        internal static int InjectUpFrame = -1, InjectDownFrame = -1,
            InjectLeftFrame = -1, InjectRightFrame = -1,
            InjectConfirmFrame = -1, InjectCancelFrame = -1;

        // Edge-clamp reflection (the game's own tests, read at time of use)
        private static MethodInfo _getControllerScrollableList;
        private static MethodInfo _canControllerScrollDown;
        private static MethodInfo _canControllerScrollUp;
        private static MethodInfo _getListButtonsMethod;

        public static void ApplyPatches(Harmony harmony)
        {
            if (_applied) return;
            _applied = true;

            try
            {
                var skaldIOType = AccessTools.TypeByName("SkaldIO");
                var guiControlType = AccessTools.TypeByName("GUIControl");
                var uiCanvasType = AccessTools.TypeByName("UICanvas");
                if (skaldIOType == null || guiControlType == null || uiCanvasType == null)
                {
                    Plugin.Logger?.LogError("[SkaldIO] Core types missing — input layer unavailable");
                    return;
                }

                // (1) Forced controller mode
                var connected = AccessTools.Method(skaldIOType, "isControllerConnected");
                if (connected != null)
                {
                    harmony.Patch(connected, prefix: new HarmonyMethod(typeof(SkaldIOPatches), nameof(Prefix_ForceControllerConnected)));
                    Plugin.Logger?.LogInfo("[SkaldIO] Forced controller mode ON (isControllerConnected → true)");
                }
                else
                {
                    Plugin.Logger?.LogError("[SkaldIO] isControllerConnected not found — forced controller mode unavailable");
                }

                // (2) Edge clamp
                _getControllerScrollableList = AccessTools.Method(guiControlType, "getControllerScrollableList");
                _canControllerScrollDown = AccessTools.Method(uiCanvasType, "canControllerScrollDown");
                _canControllerScrollUp = AccessTools.Method(uiCanvasType, "canControllerScrollUp");
                var aboveMethod = AccessTools.Method(guiControlType, "setMouseToClosestOptionAbove");
                var belowMethod = AccessTools.Method(guiControlType, "setMouseToClosestOptionBelow");
                if (_getControllerScrollableList != null && _canControllerScrollDown != null
                    && _canControllerScrollUp != null && aboveMethod != null && belowMethod != null)
                {
                    harmony.Patch(aboveMethod, prefix: new HarmonyMethod(typeof(SkaldIOPatches), nameof(Prefix_ClampAbove)));
                    harmony.Patch(belowMethod, prefix: new HarmonyMethod(typeof(SkaldIOPatches), nameof(Prefix_ClampBelow)));
                    Plugin.Logger?.LogInfo("[SkaldIO] Edge clamp on setMouseToClosestOptionAbove/Below (B4)");
                }
                else
                {
                    Plugin.Logger?.LogError("[SkaldIO] Edge-clamp targets missing — native edge scroll runs silent (B4 open)");
                }

                // (3) Keybindings-screen scrollable-list fallback
                var sheetComplexSettingsType = AccessTools.TypeByName("GUIControl+SheetComplexSettings");
                if (sheetComplexSettingsType != null)
                {
                    _getListButtonsMethod = AccessTools.Method(sheetComplexSettingsType, "getListButtons");
                    var getListMethod = AccessTools.Method(sheetComplexSettingsType, "getControllerScrollableList");
                    if (getListMethod != null && _getListButtonsMethod != null)
                    {
                        harmony.Patch(getListMethod, postfix: new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_SettingsScrollableList)));
                        Plugin.Logger?.LogInfo("[SkaldIO] SheetComplexSettings listButtons fallback");
                    }
                }

                // (4) Review-layer key swallow (WP10): while the review toggle
                // is open (or during the eat tail of its closing press), the
                // game goes blind to captured keys — one choke point covers
                // every binding-mediated read. getPressedEscapeKey reads its
                // list directly and needs its own postfix.
                foreach (string reader in new[] { "getKeyPressed", "getKeyHeldDown", "getKeyUp" })
                {
                    var m = AccessTools.Method(skaldIOType, reader, new[] { typeof(UnityEngine.KeyCode) });
                    if (m != null)
                        harmony.Patch(m, postfix: new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_SwallowKey)));
                    else
                        Plugin.Logger?.LogError($"[SkaldIO] {reader} not found — review swallow incomplete");
                }
                var escMethod = AccessTools.Method(skaldIOType, "getPressedEscapeKey");
                if (escMethod != null)
                    harmony.Patch(escMethod, postfix: new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_SwallowEscape)));
                Plugin.Logger?.LogInfo("[SkaldIO] Review-layer key swallow armed");

                // (5) Keyboard → controller feed
                ControllerFeedPatch.Apply(harmony);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[SkaldIO] Failed to apply patches: {ex.Message}");
            }
        }

        static bool Prefix_ForceControllerConnected(ref bool __result)
        {
            __result = true;
            return false;
        }

        static void Postfix_SwallowKey(UnityEngine.KeyCode __0, ref bool __result)
        {
            if (__result && ReviewLayer.ShouldSwallowKey(__0)) __result = false;
        }

        static void Postfix_SwallowEscape(ref bool __result)
        {
            if (__result && ReviewLayer.ShouldSwallowKey(UnityEngine.KeyCode.Escape)) __result = false;
        }

        /// <summary>Above = decrement; the game's own canControllerScrollDown
        /// (index &gt; 0) is the edge test, asked at time of use.</summary>
        static bool Prefix_ClampAbove(object __instance)
        {
            return ObserveEdge(__instance, _canControllerScrollDown, "Top of list.");
        }

        /// <summary>Below = increment; canControllerScrollUp (index &lt; count-1).</summary>
        static bool Prefix_ClampBelow(object __instance)
        {
            return ObserveEdge(__instance, _canControllerScrollUp, "Bottom of list.");
        }

        /// <summary>B4 as amended (2026-08-16, class-list ride): at a window edge
        /// the native branch slides the PAGED WINDOW one entry — on long lists
        /// that is the only route to entries beyond the page (the original B4
        /// "never load-bearing" claim was wrong there). So the native scroll
        /// always runs; we capture the focused slot's composed line first, and
        /// the drain compares after the re-render — content changed means a new
        /// entry slid under focus (speak it), unchanged means a true end
        /// (speak the edge line). Never a silent press either way.</summary>
        private static bool ObserveEdge(object instance, MethodInfo canScroll, string edgeText)
        {
            try
            {
                object list = _getControllerScrollableList.Invoke(instance, null);
                if (list == null) return true;                 // original no-ops
                if ((bool)canScroll.Invoke(list, null)) return true; // in-window move; join speaks
                Pump.NoteEdgeScroll(list, Pump.CurrentLineOf(list), edgeText);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[SkaldIO:edge] {ex.Message}");
            }
            return true; // the native window-slide always runs
        }

        /// <summary>When SheetComplexSettings.getControllerScrollableList()
        /// returns null (sliderControl unset — the keybindings screen), fall
        /// back to the sheet's own listButtons.</summary>
        static void Postfix_SettingsScrollableList(object __instance, ref object __result)
        {
            if (__result != null) return;
            try { __result = _getListButtonsMethod.Invoke(__instance, null); }
            catch { }
        }
    }
}

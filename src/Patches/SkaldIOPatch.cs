using HarmonyLib;
using System;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Forced controller mode (build-plan WP7, owner ruling 2026-08-16 —
    /// supersedes the March gate-removal layer).
    ///
    /// 1. SkaldIO.isControllerConnected → true. Every native gate site flips on
    ///    (option funnel, popup scrolling, virtual-mouse snapping, D-pad ability
    ///    row, single-click grid semantics) — the Steam Deck hard-wires this
    ///    exact mode, so it is a shipped first-class configuration. Keyboard
    ///    reaches the controller layer via ControllerFeedPatch.
    ///
    /// 2. Edge observer (bug-ledger B4 as amended 2026-08-16): at a window edge
    ///    the native setMouseToClosestOptionAbove/Below branch slides the paged
    ///    window — on long lists the only route past the page. The native slide
    ///    always runs; the drain diffs the focused slot pre/post and speaks the
    ///    revealed entry or the true-edge line.
    ///
    /// 3. SheetComplexSettings.getControllerScrollableList null-fallback: the
    ///    keybindings screen has no sliderControl and natively returns null,
    ///    dead-ending controller navigation there — listButtons is the game's
    ///    own structure, wired in.
    ///
    /// 4. Review-layer key swallow (WP10) at the SkaldIO choke point.
    ///
    /// Applied from Plugin.Update after the first state classification —
    /// SkaldIO's static constructor needs game data (C64Color) loaded.
    /// All game handles come from the WP8 Seams registry (resolved at Awake,
    /// metadata-only); a missing handle costs that one feature, logged there.
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

        public static void ApplyPatches(Harmony harmony)
        {
            if (_applied) return;
            _applied = true;

            try
            {
                // (1) Forced controller mode
                if (Seams.SkaldIO_isControllerConnected != null)
                {
                    harmony.Patch(Seams.SkaldIO_isControllerConnected,
                        prefix: new HarmonyMethod(typeof(SkaldIOPatches), nameof(Prefix_ForceControllerConnected)));
                    Plugin.Logger?.LogInfo("[SkaldIO] Forced controller mode ON (isControllerConnected → true)");
                }
                else
                {
                    Plugin.Logger?.LogError("[SkaldIO] isControllerConnected seam missing — forced controller mode unavailable");
                }

                // (2) Edge observer
                if (Seams.GUIControl_getControllerScrollableList != null
                    && Seams.UICanvas_canControllerScrollDown != null
                    && Seams.UICanvas_canControllerScrollUp != null
                    && Seams.GUIControl_setMouseToClosestOptionAbove != null
                    && Seams.GUIControl_setMouseToClosestOptionBelow != null)
                {
                    harmony.Patch(Seams.GUIControl_setMouseToClosestOptionAbove,
                        prefix: new HarmonyMethod(typeof(SkaldIOPatches), nameof(Prefix_ClampAbove)));
                    harmony.Patch(Seams.GUIControl_setMouseToClosestOptionBelow,
                        prefix: new HarmonyMethod(typeof(SkaldIOPatches), nameof(Prefix_ClampBelow)));
                    Plugin.Logger?.LogInfo("[SkaldIO] Edge observer on setMouseToClosestOptionAbove/Below (B4)");
                }
                else
                {
                    Plugin.Logger?.LogError("[SkaldIO] Edge-observer seams missing — native edge scroll runs silent (B4 open)");
                }

                // (3) Keybindings-screen scrollable-list fallback
                if (Seams.SheetComplexSettings_getControllerScrollableList != null
                    && Seams.SheetComplexSettings_getListButtons != null)
                {
                    harmony.Patch(Seams.SheetComplexSettings_getControllerScrollableList,
                        postfix: new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_SettingsScrollableList)));
                    Plugin.Logger?.LogInfo("[SkaldIO] SheetComplexSettings listButtons fallback");
                }

                // (4) Review-layer key swallow (WP10): while the review toggle
                // is open (or during the eat tail of its closing press), the
                // game goes blind to captured keys — one choke point covers
                // every binding-mediated read. getPressedEscapeKey reads its
                // list directly and needs its own postfix.
                int swallow = 0;
                foreach (var reader in new[] { Seams.SkaldIO_getKeyPressed, Seams.SkaldIO_getKeyHeldDown, Seams.SkaldIO_getKeyUp })
                {
                    if (reader == null) continue;
                    harmony.Patch(reader, postfix: new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_SwallowKey)));
                    swallow++;
                }
                if (Seams.SkaldIO_getPressedEscapeKey != null)
                {
                    harmony.Patch(Seams.SkaldIO_getPressedEscapeKey,
                        postfix: new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_SwallowEscape)));
                    swallow++;
                }
                if (swallow == 4)
                    Plugin.Logger?.LogInfo("[SkaldIO] Review-layer key swallow armed");
                else
                    Plugin.Logger?.LogError($"[SkaldIO] Review swallow incomplete ({swallow}/4 readers) — see seam report");

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
            return ObserveEdge(__instance, Seams.UICanvas_canControllerScrollDown, "Top of list.");
        }

        /// <summary>Below = increment; canControllerScrollUp (index &lt; count-1).</summary>
        static bool Prefix_ClampBelow(object __instance)
        {
            return ObserveEdge(__instance, Seams.UICanvas_canControllerScrollUp, "Bottom of list.");
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
                object list = Seams.GUIControl_getControllerScrollableList.Invoke(instance, null);
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
            try { __result = Seams.SheetComplexSettings_getListButtons.Invoke(__instance, null); }
            catch { }
        }
    }
}

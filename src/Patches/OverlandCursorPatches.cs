using HarmonyLib;
using System;
using UnityEngine;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// WP11 Harmony patches (all applied in the DEFERRED batch from
    /// SkaldIOPatches — the WP9 lesson: never detour game types at frame 0).
    ///
    /// 1. Arrow freeing: the rebind screen rejects None, so the four
    ///    alt-movement accessors (getUpAltKey etc. — complete consumer set is
    ///    eight OR-merged reads inside SkaldIO) are postfixed to KeyCode.None.
    ///    WASD and the stick feed keep movement everywhere; bare arrows are
    ///    freed for the cursor.
    ///
    /// 2. Course join (Party-level per the adversarial review — the
    ///    Character-level methods fire once per member and clear fires even
    ///    with an empty course): "Walking, N steps." on set; "Stopped." on a
    ///    clear that had nodes. Player party only.
    ///
    /// 3. Path failure: findPathToMouseTile returns true whether or not a
    ///    course was produced — a click/Z that yields no course speaks
    ///    "No path."
    ///
    /// 4. Tooltip discipline: the inspect tooltip sits under the cursor and
    ///    silently swallows the next click (isMouseOverTooltip gates both
    ///    click branches). A prefix on OverlandState.setMouseInput clears the
    ///    tooltip whenever the cursor is held, a tooltip is up, and Z is down
    ///    — so the click that same tick lands on the map.
    /// </summary>
    public static class OverlandCursorPatches
    {
        public static void Apply(Harmony harmony)
        {
            int applied = 0;

            // (1) Arrow freeing
            foreach (var alt in new[] { Seams.KeyBindings_getUpAltKey, Seams.KeyBindings_getDownAltKey,
                                        Seams.KeyBindings_getLeftAltKey, Seams.KeyBindings_getRightAltKey })
            {
                if (alt == null) continue;
                try
                {
                    harmony.Patch(alt, postfix: new HarmonyMethod(typeof(OverlandCursorPatches), nameof(Postfix_AltKeyNone)));
                    applied++;
                }
                catch (Exception ex) { Plugin.Logger?.LogError($"[Cursor] Alt-key free failed: {ex.Message}"); }
            }
            if (applied == 4)
                Plugin.Logger?.LogInfo("[Cursor] Arrows freed (4/4 alt-movement accessors → None)");
            else
                Plugin.Logger?.LogError($"[Cursor] Arrows partially freed ({applied}/4) — cursor may fight native movement");

            // (2) Course join
            try
            {
                if (Seams.Party_setNavigationCourse != null && Seams.Party_clearNavigationCourse != null)
                {
                    harmony.Patch(Seams.Party_setNavigationCourse,
                        postfix: new HarmonyMethod(typeof(OverlandCursorPatches), nameof(Postfix_SetCourse)));
                    harmony.Patch(Seams.Party_clearNavigationCourse,
                        prefix: new HarmonyMethod(typeof(OverlandCursorPatches), nameof(Prefix_ClearCourse)),
                        postfix: new HarmonyMethod(typeof(OverlandCursorPatches), nameof(Postfix_ClearCourse)));
                    Plugin.Logger?.LogInfo("[Cursor] Course join live (Party set/clear)");
                }
                else Plugin.Logger?.LogError("[Cursor] Course seams missing — travel unvoiced");
            }
            catch (Exception ex) { Plugin.Logger?.LogError($"[Cursor] Course join failed: {ex.Message}"); }

            // (3) Path failure
            try
            {
                if (Seams.Map_findPathToMouseTile != null)
                {
                    harmony.Patch(Seams.Map_findPathToMouseTile,
                        postfix: new HarmonyMethod(typeof(OverlandCursorPatches), nameof(Postfix_FindPath)));
                }
            }
            catch (Exception ex) { Plugin.Logger?.LogError($"[Cursor] Path-fail join failed: {ex.Message}"); }

            // (4) Tooltip discipline
            try
            {
                if (Seams.OverlandState_setMouseInput != null)
                {
                    harmony.Patch(Seams.OverlandState_setMouseInput,
                        prefix: new HarmonyMethod(typeof(OverlandCursorPatches), nameof(Prefix_SetMouseInput)));
                }
            }
            catch (Exception ex) { Plugin.Logger?.LogError($"[Cursor] Tooltip-clear prefix failed: {ex.Message}"); }
        }

        static void Postfix_AltKeyNone(ref KeyCode __result)
        {
            __result = KeyCode.None;
        }

        private static bool IsPlayerParty(object party)
        {
            try
            {
                if (Seams.MainControl_getDataControl == null || Seams.DataControl_currentMap == null
                    || Seams.Map_playerParty == null) return false;
                object dc = Seams.MainControl_getDataControl.Invoke(null, null);
                object map = dc != null ? Seams.DataControl_currentMap.GetValue(dc) : null;
                return map != null && ReferenceEquals(Seams.Map_playerParty.GetValue(map), party);
            }
            catch { return false; }
        }

        static void Postfix_SetCourse(object __instance, object __0)
        {
            try
            {
                if (!IsPlayerParty(__instance) || __0 == null || Seams.NavigationCourse_getLength == null) return;
                int len = (int)Seams.NavigationCourse_getLength.Invoke(__0, null);
                if (len > 0) Pump.NoteTravel(len == 1 ? "Walking, 1 step." : $"Walking, {len} steps.");
            }
            catch { }
        }

        static void Prefix_ClearCourse(object __instance, out bool __state)
        {
            __state = false;
            try
            {
                if (Seams.Party_navigationCourseHasNodes != null)
                    __state = (bool)Seams.Party_navigationCourseHasNodes.Invoke(__instance, null);
            }
            catch { }
        }

        static void Postfix_ClearCourse(object __instance, bool __state)
        {
            try
            {
                if (__state && IsPlayerParty(__instance)) Pump.NoteTravel("Stopped.");
            }
            catch { }
        }

        static void Postfix_FindPath(object __instance)
        {
            try
            {
                if (Seams.Map_playerParty == null || Seams.Party_navigationCourseHasNodes == null) return;
                object party = Seams.Map_playerParty.GetValue(__instance);
                if (party != null && !(bool)Seams.Party_navigationCourseHasNodes.Invoke(party, null))
                    Pump.NoteTravel("No path.");
            }
            catch { }
        }

        /// <summary>Runs each overland tick before the click branches sample
        /// the tooltip-hover gate.</summary>
        static void Prefix_SetMouseInput()
        {
            try
            {
                if (!Input.GetKeyDown(KeyCode.Z) && !Input.GetKey(KeyCode.Z)) return;
                if (Seams.ToolTipPrinter_hasToolTip != null
                    && (bool)Seams.ToolTipPrinter_hasToolTip.Invoke(null, null))
                    Seams.ToolTipPrinter_clearToolTip?.Invoke(null, null);
            }
            catch { }
        }
    }
}

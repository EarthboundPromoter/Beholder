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
    /// 2. Funnel index walk + edge observer (2026-09-02; B4 as amended
    ///    2026-08-16): an in-window setMouseToClosestOptionAbove/Below press
    ///    steps the canvas's remembered index directly and snaps (the native
    ///    hover walk stalls silently whenever hover is lost); at a window edge
    ///    the native branch slides the paged window — on long lists the only
    ///    route past the page. That slide always runs; the drain diffs the
    ///    focused slot pre/post and speaks the revealed entry or the edge line.
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
        // Numeric option-row drive (/press?key=1..9): consumed one-shot by the
        // getNumericButtonPressIndex postfix — the exact read the states poll,
        // so a synthetic numeric press has full native parity.
        internal static int InjectNumericFrame = -1, InjectNumericIndex = -1;

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
                    Plugin.Logger?.LogInfo("[SkaldIO] Funnel index walk + edge observer on setMouseToClosestOptionAbove/Below");
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

                // (4b) Bridge numeric drive: one-shot injected option-row press
                // at the game's own read point (dev-only, inert without the
                // bridge arming the fields).
                if (Seams.GUIControl_getNumericButtonPressIndex != null)
                {
                    harmony.Patch(Seams.GUIControl_getNumericButtonPressIndex,
                        postfix: new HarmonyMethod(typeof(SkaldIOPatches), nameof(Postfix_InjectNumeric)));
                }

                // (4c) RETIRED (regression a8de251-class, caught by owner
                // 2026-08-17 same day): the player-nav stamp briefly patched
                // the four one-line SkaldIO option-selection WRAPPERS —
                // Harmony's stub for a patched wrapper is a fresh JIT of its
                // IL, and Mono freely inlines the inner accessor's PRISTINE
                // IL into that stub, bypassing the ControllerFeed detour on
                // the inner. Result: keyboard input died per-caller by JIT
                // inline budget (A dead, W/S/D alive on the CC editor).
                // LESSON AT FILE SCOPE: never add a Harmony detour to a
                // one-line SkaldIO wrapper — stamp or observe at the layer
                // that is ALREADY detoured (ControllerFeedPatch), or at a
                // body big enough that its callers call rather than inline.
                // The stamp now lives inside ControllerFeedPatch's own
                // stick postfixes.

                // (5) Keyboard → controller feed (carries the player-nav
                // stamp — see ControllerFeedPatch)
                ControllerFeedPatch.Apply(harmony);

                // (6) Grid-modal movement suppression (WP9) — SkaldIO readers,
                // so they belong to this deferred batch like every SkaldIO
                // patch (frame-0 detours force the cctor — the boot-kill).
                GridNavigationPatch.ApplyMovementSuppression(harmony);

                // (7) Overland cursor patches (WP11) — game-type detours, so
                // deferred like everything else here.
                OverlandCursorPatches.Apply(harmony);

                // (8) Mouse guard (owner ruling 2026-08-17) — SkaldIO detours,
                // so deferred; latches every keyboard snap against physical
                // jitter and right-stick drift.
                MouseGuardPatch.Apply(harmony);

                // (8b) Rest-drift guard + walk watchdog (click-to-move report
                // 2026-09-03) — nested SkaldIO detours, so deferred.
                InputRestGuardPatch.Apply(harmony);

                // (9) Combat cursor prefixes (CP3) — game-type detours on the
                // combat states' mouse branches, so deferred like the rest.
                CombatCursorPatches.Apply(harmony);

                // (10) Scrollbar arrow reclaim (table-UI foundation, gate
                // item 1) — SkaldIO + UIScrollbar detours, so deferred.
                ScrollbarReclaimPatch.Apply(harmony);

                // (11) Table-engine section redirect (table-ui gate A) —
                // game-type detour on SheetClass, so deferred like the rest.
                TableRedirectPatch.Apply(harmony);
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
            if (__result && (KeyTable.ShouldSwallowKey(__0)
                || ReviewLayer.ShouldSwallowKey(__0) || OverlandCursor.ShouldSwallowKey(__0)
                || CombatCursor.ShouldSwallowKey(__0) || TableCursor.ShouldSwallowKey(__0) || ArrowClaimed(__0)))
                __result = false;
        }

        /// <summary>The mod's arrow claim (owner ruling 2026-08-17): in
        /// overland- and combat-class states the game goes blind to the four
        /// arrow keys at this choke — every binding-mediated read answers
        /// false, so no bind (the alt-movement "Move 2" set included) can
        /// move the character on arrows. Replaces reliance on the WP11
        /// alt-getter patches, whose one-line targets the JIT inlines into
        /// callers compiled before the deferred batch lands (the regression
        /// the owner caught live). The mod's own arrow reads use raw input
        /// and never pass through here; raw game consumers (tooltip scroll,
        /// console history, rebind capture) are untouched by design. Combat
        /// arrows were a pure WASD duplicate; the combat cursor inherits the
        /// claim when it ships.</summary>
        static bool ArrowClaimed(UnityEngine.KeyCode key)
        {
            if (key != UnityEngine.KeyCode.UpArrow && key != UnityEngine.KeyCode.DownArrow
                && key != UnityEngine.KeyCode.LeftArrow && key != UnityEngine.KeyCode.RightArrow)
                return false;
            try
            {
                object state = Pump.CurrentStateObject();
                if (state == null) return false;
                return (Seams.OverlandStateType != null && Seams.OverlandStateType.IsInstanceOfType(state))
                    || (Seams.CombatBaseStateType != null && Seams.CombatBaseStateType.IsInstanceOfType(state))
                    // CP3 (A7): CombatPlacementState extends StateBase, NOT
                    // CombatBaseState — without this the arrows still move the
                    // placing PC (survey §7b claim-scope correction).
                    || (Seams.CombatPlacementStateType != null && Seams.CombatPlacementStateType.IsInstanceOfType(state));
            }
            catch { return false; }
        }

        /// <summary>One-shot bridge numeric press: fires only on the armed frame
        /// and only when no real press happened (__result stays authoritative).</summary>
        static void Postfix_InjectNumeric(ref int __result)
        {
            // Read from the game's tick (StateBase.update): fires on the first
            // tick at or after the armed frame, never on frame equality — a
            // tick need not land in that exact render frame (press-latch
            // audit 2026-09-02). One-shot by its own reset.
            if (__result != -1 || InjectNumericFrame < 0 || UnityEngine.Time.frameCount < InjectNumericFrame) return;
            __result = InjectNumericIndex;
            InjectNumericFrame = -1;
        }

        /// <summary>Plugin.Update relay to the feed's press latch (kept here so
        /// Plugin talks to one patch surface).</summary>
        internal static void ArmPressLatch() => ControllerFeedPatch.ArmLatch();

        static void Postfix_SwallowEscape(ref bool __result)
        {
            if (__result && (KeyTable.ShouldSwallowKey(UnityEngine.KeyCode.Escape)
                || ReviewLayer.ShouldSwallowKey(UnityEngine.KeyCode.Escape)
                || OverlandCursor.ShouldSwallowKey(UnityEngine.KeyCode.Escape)))
                __result = false;
        }

        /// <summary>Above = decrement; the game's own canControllerScrollDown
        /// (index &gt; 0) is the edge test, asked at time of use.</summary>
        static bool Prefix_ClampAbove(object __instance)
        {
            return FunnelStep(__instance, Seams.UICanvas_canControllerScrollDown, -1, "Top of list.");
        }

        /// <summary>Below = increment; canControllerScrollUp (index &lt; count-1).</summary>
        static bool Prefix_ClampBelow(object __instance)
        {
            return FunnelStep(__instance, Seams.UICanvas_canControllerScrollUp, +1, "Bottom of list.");
        }

        /// <summary>The index walk (owner ruling 2026-09-02, off Shane's
        /// 0.5.5 log): an in-window arrow press moves the canvas's OWN
        /// remembered index by one and snaps the mouse to it — never the
        /// native hover walk. The native increment/decrement (UICanvas.cs:39-69)
        /// finds the HOVERED element and steps from there; with nothing hovered
        /// it silently no-ops and the snap merely re-parks on the remembered
        /// row. Hover is lost by any physical-mouse displacement past the
        /// guard's threshold (an alt-tab, a desk bump, a drifting sensor) and
        /// by a popup close re-parking the cursor — so a press after any of
        /// those moved nothing and spoke nothing: "sometimes one press moved,
        /// other times I had to hit up arrow three or four times" (Shane,
        /// main menu / creation / loot, 2026-09-02). The remembered index is
        /// the cursor of record (no mouse→index sync, standing ruling); hover
        /// follows the snap, never leads it.
        ///
        /// Scope: canvases whose increment/decrement are the UICanvas base
        /// (numeric/list button rows, sheets, slider controls). A canvas that
        /// OVERRIDES them (UIAbilitySelectorGrid's row-width stride,
        /// UIInventorySheetBase's surface delegation) keeps its native walk —
        /// those carry semantics the flat step must not flatten. The edge
        /// branch is untouched: the native window-slide runs and the observer
        /// speaks the revealed entry or the edge line (B4 as amended).
        /// Returns false when the step was taken here (native skipped).</summary>
        private static bool FunnelStep(object instance, MethodInfo canScroll, int delta, string edgeText)
        {
            try
            {
                object list = Seams.GUIControl_getControllerScrollableList.Invoke(instance, null);
                if (list == null) return true;                 // original no-ops
                if (!(bool)canScroll.Invoke(list, null))
                {
                    Pump.NoteEdgeScroll(list, Pump.CurrentLineOf(list), edgeText);
                    return true; // the native window-slide always runs
                }
                if (!BaseIndexWalk(list)) return true;         // overriding canvas: native walk
                if (Seams.UICanvas_getCurrentSelectedButtonIndex == null
                    || Seams.UICanvas_setCurrentSelectedButton == null
                    || Seams.UICanvas_getScrollableElements == null
                    || Seams.GUIControl_setMouseToSelectedOption == null) return true;

                int index = (int)Seams.UICanvas_getCurrentSelectedButtonIndex.Invoke(list, null);
                int count = (Seams.UICanvas_getScrollableElements.Invoke(list, null)
                    as System.Collections.ICollection)?.Count ?? 0;
                if (count <= 0) return true;
                int next = index + delta;
                if (next < 0) next = 0;
                else if (next >= count) next = count - 1;
                Seams.UICanvas_setCurrentSelectedButton.Invoke(list, new object[] { next });
                Seams.GUIControl_setMouseToSelectedOption.Invoke(instance, null);
                Scaffold.Log.Debug("Funnel", $"{list.GetType().Name} index {index}->{next} of {count}");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[SkaldIO:funnel] {ex.Message}");
            }
            return true;
        }

        private static readonly System.Collections.Generic.Dictionary<Type, bool> _baseWalk
            = new System.Collections.Generic.Dictionary<Type, bool>();

        /// <summary>True when this canvas's increment AND decrement are the
        /// UICanvas base bodies (hover walks) — the only bodies the flat index
        /// step is equivalent to. Cached per runtime type.</summary>
        internal static bool BaseIndexWalk(object canvas)
        {
            Type t = canvas.GetType();
            if (_baseWalk.TryGetValue(t, out bool b)) return b;
            bool result = false;
            try
            {
                var inc = t.GetMethod("incrementCurrentSelectedButton", BindingFlags.Public | BindingFlags.Instance);
                var dec = t.GetMethod("decrementCurrentSelectedButton", BindingFlags.Public | BindingFlags.Instance);
                result = inc != null && dec != null && Seams.UICanvasType != null
                    && inc.DeclaringType == Seams.UICanvasType && dec.DeclaringType == Seams.UICanvasType;
            }
            catch { }
            _baseWalk[t] = result;
            return result;
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

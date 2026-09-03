using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// The rest-drift guard and the walk watchdog (click-to-move report,
    /// 2026-09-03; ledger B21).
    ///
    /// The overland auto-walk advances a clicked course only on ticks where
    /// SkaldIO.anyKeyDown() is false (OverlandState.cs:113-133: the held
    /// branch steps with WASD, the else branch pops the course). That check
    /// has two halves: the raw held-key list (every Unity key code — keys,
    /// mouse buttons, joystick buttons) and
    /// ControllerInputControl.isAnyControllerButtonPressed, which answers
    /// true for ANY non-zero left-stick or d-pad axis (SkaldIO.cs:181-188).
    /// The game's own stick containers call the stick held only past
    /// leftStickThreshold (0.5, SkaldIO.cs:143-146), so a joystick resting
    /// between Unity's 0.19 dead zone and 0.5 — a worn pad, a wheel's
    /// pedals, a throttle — moves nothing, changes nothing visible, and
    /// silently freezes every clicked course forever. Under forced controller
    /// mode a plugged-in pad is otherwise invisible, so the player sees only
    /// "click-to-move stopped working" (the 2026-09-03 log: every click set a
    /// course, the party never left its tile, WASD stepped fine).
    ///
    /// 1. The guard: a postfix re-answers isAnyControllerButtonPressed with
    ///    the game's own threshold — d-pad exact, stick past
    ///    leftStickThreshold (read from the game's field). A stick pushed
    ///    past it still pauses the walk; a held button still pauses the walk
    ///    (the held-key half is untouched — a deliberate hold is native).
    ///    Two consumers: the overland walk branch and the map renderer's
    ///    mouse-tile highlight (MapIllustrator.cs:1270).
    /// 2. Receipts: joystick names on the apply line; one Info line per
    ///    session the first time resting drift is discarded, with the values.
    /// 3. The watchdog: a prefix on SkaldIO.clear (before the game empties
    ///    its lists) counts ticks a player course sits with the party on the
    ///    same tile while no popup or cutscene suspends the state (those
    ///    pause the walk legitimately — the map-edge prompt at a route's end
    ///    is the common case; Opus review 2026-09-03); at 60 ticks it logs
    ///    once per stall episode (a course
    ///    that resumes and stalls on another tile logs again) what the game
    ///    sees as held — the any-input verdict, the held key codes, the
    ///    stick and d-pad values, the console flag — so the next report
    ///    names its cause from the log. anyKeyDown=false with an empty held
    ///    list means the stall is not input at all (animation, step clock).
    ///
    /// Applied from the deferred SkaldIO batch (nested-type detours force the
    /// cctor). Seam-gated (WP8).
    /// </summary>
    public static class InputRestGuardPatch
    {
        private static float _threshold = 0.5f;
        private static bool _driftReported;
        // Behavioural receipt (Sonnet review 2026-09-03, the Mono-inline
        // lesson): the postfix's FIRST invocation logs once, unconditionally,
        // so a log distinguishes "detour never ran" from "ran, no drift".
        private static bool _invocationSeen;
        private static bool _guardFaultLogged, _watchFaultLogged;
        internal static int DriftDiscards;          // bridge /input reports it

        // Watchdog state
        private const int WatchTicks = 60;          // one second of game logic
        private static bool _watchArmed, _watchReported;
        private static int _watchX, _watchY, _watchTicks;

        internal static void Apply(Harmony harmony)
        {
            string sticks = JoystickNames();
            if (Seams.CIC_isAnyControllerButtonPressed == null
                || Seams.CIC_getLeftJoystickPosition == null
                || Seams.CIC_getDpadPosition == null)
            {
                Plugin.Logger?.LogError($"[Input] Rest-drift guard seams missing — a drifting joystick freezes click-to-move; joysticks: {sticks}");
                return;
            }
            try
            {
                if (Seams.CIC_leftStickThreshold != null)
                    _threshold = (float)Seams.CIC_leftStickThreshold.GetValue(null);
            }
            catch { }
            harmony.Patch(Seams.CIC_isAnyControllerButtonPressed,
                postfix: new HarmonyMethod(typeof(InputRestGuardPatch), nameof(Postfix_AnyButton)));

            bool watch = Seams.SkaldIO_clear != null && Seams.OverlandStateType != null
                && Seams.MainControl_getDataControl != null && Seams.DataControl_currentMap != null
                && Seams.Map_playerParty != null && Seams.Party_navigationCourseHasNodes != null
                && Seams.Map_getXPos != null && Seams.Map_getYPos != null;
            if (watch)
                harmony.Patch(Seams.SkaldIO_clear,
                    prefix: new HarmonyMethod(typeof(InputRestGuardPatch), nameof(Prefix_TickWatch)));

            Plugin.Logger?.LogInfo($"[Input] Rest-drift guard on the any-input check (stick threshold {_threshold:F2}); "
                + $"walk watchdog {(watch ? "armed" : "unavailable")}; joysticks: {sticks}");
        }

        internal static string JoystickNames()
        {
            try
            {
                var names = Input.GetJoystickNames();
                var kept = new List<string>();
                foreach (var n in names) if (!string.IsNullOrEmpty(n)) kept.Add(n);
                return kept.Count == 0 ? "none" : string.Join("; ", kept.ToArray());
            }
            catch { return "unreadable"; }
        }

        /// <summary>The game's any-controller-input verdict, re-answered with
        /// the game's own stick threshold. Runs only when the raw check said
        /// true, so a clean machine never pays for it.</summary>
        static void Postfix_AnyButton(ref bool __result)
        {
            if (!_invocationSeen)
            {
                _invocationSeen = true;
                Plugin.Logger?.LogInfo($"[Input] Rest-drift guard confirmed running (first any-input check seen, raw={__result.ToString().ToLowerInvariant()})");
            }
            if (!__result) return;
            try
            {
                Vector2 dpad = (Vector2)Seams.CIC_getDpadPosition.Invoke(null, null);
                if (dpad.x != 0f || dpad.y != 0f) return;
                Vector2 stick = (Vector2)Seams.CIC_getLeftJoystickPosition.Invoke(null, null);
                if (Mathf.Abs(stick.x) > _threshold || Mathf.Abs(stick.y) > _threshold) return;
                __result = false;
                DriftDiscards++;
                if (!_driftReported)
                {
                    _driftReported = true;
                    Plugin.Logger?.LogInfo($"[Input] Joystick rest drift ignored: H={stick.x:0.00} V={stick.y:0.00} "
                        + $"(below the game's stick threshold {_threshold:F2}; the walk would otherwise never advance)");
                }
            }
            catch (Exception ex)
            {
                if (!_guardFaultLogged) { _guardFaultLogged = true; Plugin.Logger?.LogError($"[Input:guard] {ex.Message} (reported once)"); }
            }
        }

        /// <summary>Before the game empties its per-tick input lists: while a
        /// player course exists and the party has not left its tile, count
        /// ticks; at WatchTicks log once what the game sees as held.</summary>
        static void Prefix_TickWatch()
        {
            try
            {
                object state = Pump.CurrentStateObject();
                if (state == null || !Seams.OverlandStateType.IsInstanceOfType(state)) { Reset(); return; }
                // A popup or a cutscene suspends state.update (MainControl's
                // StateControl.update: the popup branch, else-if
                // !hasCutScene), so the course legitimately does not advance
                // — the map-edge prompt at the end of a route is the common
                // case. Counting through it named a benign stall as an input
                // stall (Opus review 2026-09-03). The count restarts when the
                // state resumes.
                if (PopupUp() || CutsceneUp()) { Reset(); return; }
                object dc = Seams.MainControl_getDataControl.Invoke(null, null);
                object map = dc != null ? Seams.DataControl_currentMap.GetValue(dc) : null;
                object party = map != null ? Seams.Map_playerParty.GetValue(map) : null;
                if (party == null || !(bool)Seams.Party_navigationCourseHasNodes.Invoke(party, null)) { Reset(); return; }

                int px = (int)Seams.Map_getXPos.Invoke(map, null);
                int py = (int)Seams.Map_getYPos.Invoke(map, null);
                if (!_watchArmed || px != _watchX || py != _watchY)
                {
                    _watchArmed = true; _watchX = px; _watchY = py; _watchTicks = 0; _watchReported = false;
                    return;
                }
                _watchTicks++;
                if (_watchTicks < WatchTicks || _watchReported) return;
                _watchReported = true;

                bool any = Seams.SkaldIO_anyKeyDown != null && (bool)Seams.SkaldIO_anyKeyDown.Invoke(null, null);
                Vector2 stick = (Vector2)Seams.CIC_getLeftJoystickPosition.Invoke(null, null);
                Vector2 dpad = (Vector2)Seams.CIC_getDpadPosition.Invoke(null, null);
                Plugin.Logger?.LogInfo($"[Input] Course unwalked for {_watchTicks} ticks at ({px},{py}): "
                    + $"anyKeyDown={any} keysHeld=[{HeldKeys()}] stick H={stick.x:0.00} V={stick.y:0.00} "
                    + $"dpad {dpad.x:0}/{dpad.y:0} console={ConsoleOpen()} textEntry={ControllerFeedPatch.TextEntryActive().ToString().ToLowerInvariant()} "
                    + $"joysticks: {JoystickNames()}");
            }
            catch (Exception ex)
            {
                if (!_watchFaultLogged) { _watchFaultLogged = true; Plugin.Logger?.LogError($"[Input:watch] {ex.Message} (reported once)"); }
            }
        }

        /// <summary>The game's console flag: while it is open the tick skips
        /// gameControl.update entirely (MainControl.cs:654), so a stalled
        /// course then has nothing to do with held input — the line says so.
        /// The other silent stalls (a dynamic animation, the step clock,
        /// physics settle) have no cheap seam; a report with anyKeyDown=false
        /// and an empty held list points at those, not at input.</summary>
        private static string ConsoleOpen()
        {
            try
            {
                if (Seams.ConsoleControl_console == null) return "?";
                return ((bool)Seams.ConsoleControl_console.GetValue(null)).ToString().ToLowerInvariant();
            }
            catch { return "?"; }
        }

        private static void Reset()
        {
            _watchArmed = false;
            _watchTicks = 0;
            _watchReported = false;
        }

        private static bool PopupUp()
        {
            try
            {
                return Seams.PopUpControl_getCurrentPopUp != null
                    && Seams.PopUpControl_getCurrentPopUp.Invoke(null, null) != null;
            }
            catch { return false; }
        }

        private static bool CutsceneUp()
        {
            try
            {
                return Seams.CutSceneControl_hasCutScene != null
                    && (bool)Seams.CutSceneControl_hasCutScene.Invoke(null, null);
            }
            catch { return false; }
        }

        /// <summary>The game's own held-key list (raw Input.GetKey over every
        /// key code, accumulated per render frame until the tick clears it),
        /// de-duplicated.</summary>
        internal static string HeldKeys()
        {
            try
            {
                var list = Seams.SkaldIO_keyHeldDown?.GetValue(null) as System.Collections.IList;
                if (list == null) return "?";
                var seen = new HashSet<string>();
                var names = new List<string>();
                foreach (object k in list)
                {
                    string n = k.ToString();
                    if (seen.Add(n)) names.Add(n);
                }
                return names.Count == 0 ? "" : string.Join(" ", names.ToArray());
            }
            catch { return "?"; }
        }
    }
}

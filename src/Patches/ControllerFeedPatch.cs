using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// The ControllerFeed (build-plan WP7, owner ruling 2026-08-16): keyboard
    /// keys OR into the ControllerInputControl accessors, so every native
    /// controller affordance hears the keyboard. Runs under forced controller
    /// mode (SkaldIOPatches prefixes SkaldIO.isControllerConnected → true — the
    /// Steam Deck ships that exact mode).
    ///
    /// Approved map (keymap session, 2026-08-16):
    ///   WASD  → left stick (the option funnel reads only the stick)
    ///   Enter → A          Backspace → B
    ///   U / I → X / Y      Q / E → LB / RB      Z / X → LT / RT
    ///
    /// Not emulated, per the session's rulings: Start and Back (their only
    /// consumers — quest log, quick save — keep native J / F5); the D-pad
    /// (Ctrl pair and Tab already drive its functions natively; the layout
    /// overlay is skipped). Arrows are reserved for the future review layer.
    ///
    /// Triggers ride the game's own LT/RT → mouse-click merge in SkaldIO.update
    /// (SkaldIO.cs:520-543), so Z/X get real click semantics natively —
    /// replacing the deleted March Enter/RightShift click synthesis.
    ///
    /// Every OR-in suspends while the game's own text entry is capturing
    /// (TextEntryActive — the game's flags, never a mod mirror), so typing a
    /// character name never fires controller actions.
    ///
    /// Applied from SkaldIOPatches.ApplyPatches (deferred past SkaldIO's
    /// game-data-dependent static constructor).
    /// </summary>
    public static class ControllerFeedPatch
    {
        // ---- Text-entry gate (game truth, memoized once per frame) ----
        private static FieldInfo _consoleOpenField;      // ConsoleControl.console
        private static FieldInfo _feedbackTakeInputField; // FeedbackTool.takeInput
        private static MethodInfo _getCurrentPopUp;      // PopUpControl.getCurrentPopUp
        private static bool _gateInitialized;
        private static int _gateFrame = -1;
        private static bool _gateActive;

        /// <summary>True while the game itself is capturing typed text: the Tab
        /// console, the F2 feedback tool, or a text-entry popup (PopUpName /
        /// PopUpCreateSave / PopUpSaveRename — the three getInputString
        /// consumers; a game update adding a fourth is a WP8 seam-audit row).</summary>
        public static bool TextEntryActive()
        {
            if (Time.frameCount == _gateFrame) return _gateActive;
            _gateFrame = Time.frameCount;
            _gateActive = ComputeTextEntryActive();
            return _gateActive;
        }

        private static bool ComputeTextEntryActive()
        {
            if (!_gateInitialized) InitializeGate();
            try
            {
                if (_consoleOpenField != null && (bool)_consoleOpenField.GetValue(null)) return true;
                if (_feedbackTakeInputField != null && (bool)_feedbackTakeInputField.GetValue(null)) return true;
                if (_getCurrentPopUp != null)
                {
                    object popup = _getCurrentPopUp.Invoke(null, null);
                    if (popup != null)
                    {
                        string name = popup.GetType().Name;
                        if (name == "PopUpName" || name == "PopUpCreateSave" || name == "PopUpSaveRename")
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[Feed:gate] {ex.Message}");
            }
            return false;
        }

        private static void InitializeGate()
        {
            _gateInitialized = true;
            var consoleType = AccessTools.TypeByName("ConsoleControl");
            if (consoleType != null) _consoleOpenField = AccessTools.Field(consoleType, "console");
            var feedbackType = AccessTools.TypeByName("FeedbackTool");
            if (feedbackType != null) _feedbackTakeInputField = AccessTools.Field(feedbackType, "takeInput");
            var popUpControlType = AccessTools.TypeByName("PopUpControl");
            if (popUpControlType != null) _getCurrentPopUp = AccessTools.Method(popUpControlType, "getCurrentPopUp");
            if (_consoleOpenField == null || _feedbackTakeInputField == null || _getCurrentPopUp == null)
                Plugin.Logger?.LogError("[Feed] Text-entry gate incomplete — "
                    + $"console={_consoleOpenField != null} feedback={_feedbackTakeInputField != null} popup={_getCurrentPopUp != null}");
        }

        // ---- Patch application ----

        public static void Apply(Harmony harmony)
        {
            var type = AccessTools.TypeByName("SkaldIO+ControllerInputControl");
            if (type == null)
            {
                Plugin.Logger?.LogError("[Feed] SkaldIO+ControllerInputControl not found — keyboard feed unavailable");
                return;
            }

            int applied = 0;
            applied += Patch(harmony, type, "buttonAPressed", nameof(Postfix_ButtonA));
            applied += Patch(harmony, type, "buttonBPressed", nameof(Postfix_ButtonB));
            applied += Patch(harmony, type, "buttonXPressed", nameof(Postfix_ButtonX));
            applied += Patch(harmony, type, "buttonYPressed", nameof(Postfix_ButtonY));
            applied += Patch(harmony, type, "leftBumperPressed", nameof(Postfix_LeftBumper));
            applied += Patch(harmony, type, "rightBumperPressed", nameof(Postfix_RightBumper));
            applied += Patch(harmony, type, "leftTriggerPressed", nameof(Postfix_LeftTriggerPressed));
            applied += Patch(harmony, type, "leftTriggerHeld", nameof(Postfix_LeftTriggerHeld));
            applied += Patch(harmony, type, "leftTriggerUp", nameof(Postfix_LeftTriggerUp));
            applied += Patch(harmony, type, "rightTriggerPressed", nameof(Postfix_RightTriggerPressed));
            applied += Patch(harmony, type, "rightTriggerHeld", nameof(Postfix_RightTriggerHeld));
            applied += Patch(harmony, type, "rightTriggerUp", nameof(Postfix_RightTriggerUp));
            applied += Patch(harmony, type, "isLeftStickUpPressed", nameof(Postfix_StickUpPressed));
            applied += Patch(harmony, type, "isLeftStickUpHeld", nameof(Postfix_StickUpHeld));
            applied += Patch(harmony, type, "isLeftStickDownPressed", nameof(Postfix_StickDownPressed));
            applied += Patch(harmony, type, "isLeftStickDownHeld", nameof(Postfix_StickDownHeld));
            applied += Patch(harmony, type, "isLeftStickLeftPressed", nameof(Postfix_StickLeftPressed));
            applied += Patch(harmony, type, "isLeftStickLeftHeld", nameof(Postfix_StickLeftHeld));
            applied += Patch(harmony, type, "isLeftStickRightPressed", nameof(Postfix_StickRightPressed));
            applied += Patch(harmony, type, "isLeftStickRightHeld", nameof(Postfix_StickRightHeld));
            Plugin.Logger?.LogInfo($"[Feed] Keyboard→controller feed live: {applied}/20 accessors");
        }

        private static int Patch(Harmony harmony, Type type, string methodName, string postfixName)
        {
            var method = AccessTools.Method(type, methodName);
            if (method == null)
            {
                Plugin.Logger?.LogError($"[Feed] Accessor not found: {methodName} — that button's keyboard feed is dead");
                return 0;
            }
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(ControllerFeedPatch), postfixName));
            return 1;
        }

        // ---- OR-in postfixes ----
        // Unity's GetKeyDown/GetKey/GetKeyUp are stable within a frame, matching
        // the containers' pressed/held/released semantics without touching their
        // state machines. Bridge Inject*Frame fields (SkaldIOPatches — field
        // names are the bridge's reflection API) arm one-shot synthetic presses.

        private static bool Emulate(KeyCode key)
            => !TextEntryActive() && Input.GetKeyDown(key);

        static void Postfix_ButtonA(ref bool __result)
        {
            if (__result) return;
            if (Emulate(KeyCode.Return) || Time.frameCount == SkaldIOPatches.InjectConfirmFrame) __result = true;
        }

        static void Postfix_ButtonB(ref bool __result)
        {
            if (__result) return;
            if (Emulate(KeyCode.Backspace) || Time.frameCount == SkaldIOPatches.InjectCancelFrame) __result = true;
        }

        static void Postfix_ButtonX(ref bool __result) { if (!__result && Emulate(KeyCode.U)) __result = true; }
        static void Postfix_ButtonY(ref bool __result) { if (!__result && Emulate(KeyCode.I)) __result = true; }
        static void Postfix_LeftBumper(ref bool __result) { if (!__result && Emulate(KeyCode.Q)) __result = true; }
        static void Postfix_RightBumper(ref bool __result) { if (!__result && Emulate(KeyCode.E)) __result = true; }

        static void Postfix_LeftTriggerPressed(ref bool __result) { if (!__result && Emulate(KeyCode.Z)) __result = true; }
        static void Postfix_LeftTriggerHeld(ref bool __result) { if (!__result && !TextEntryActive() && Input.GetKey(KeyCode.Z)) __result = true; }
        static void Postfix_LeftTriggerUp(ref bool __result) { if (!__result && !TextEntryActive() && Input.GetKeyUp(KeyCode.Z)) __result = true; }
        static void Postfix_RightTriggerPressed(ref bool __result) { if (!__result && Emulate(KeyCode.X)) __result = true; }
        static void Postfix_RightTriggerHeld(ref bool __result) { if (!__result && !TextEntryActive() && Input.GetKey(KeyCode.X)) __result = true; }
        static void Postfix_RightTriggerUp(ref bool __result) { if (!__result && !TextEntryActive() && Input.GetKeyUp(KeyCode.X)) __result = true; }

        static void Postfix_StickUpPressed(ref bool __result)
        {
            if (__result) return;
            if (Emulate(KeyCode.W) || Time.frameCount == SkaldIOPatches.InjectUpFrame) __result = true;
        }
        static void Postfix_StickDownPressed(ref bool __result)
        {
            if (__result) return;
            if (Emulate(KeyCode.S) || Time.frameCount == SkaldIOPatches.InjectDownFrame) __result = true;
        }
        static void Postfix_StickLeftPressed(ref bool __result)
        {
            if (__result) return;
            if (Emulate(KeyCode.A) || Time.frameCount == SkaldIOPatches.InjectLeftFrame) __result = true;
        }
        static void Postfix_StickRightPressed(ref bool __result)
        {
            if (__result) return;
            if (Emulate(KeyCode.D) || Time.frameCount == SkaldIOPatches.InjectRightFrame) __result = true;
        }
        static void Postfix_StickUpHeld(ref bool __result) { if (!__result && !TextEntryActive() && Input.GetKey(KeyCode.W)) __result = true; }
        static void Postfix_StickDownHeld(ref bool __result) { if (!__result && !TextEntryActive() && Input.GetKey(KeyCode.S)) __result = true; }
        static void Postfix_StickLeftHeld(ref bool __result) { if (!__result && !TextEntryActive() && Input.GetKey(KeyCode.A)) __result = true; }
        static void Postfix_StickRightHeld(ref bool __result) { if (!__result && !TextEntryActive() && Input.GetKey(KeyCode.D)) __result = true; }
    }
}

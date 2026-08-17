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
    /// Approved map (keymap session 2026-08-16; Enter→A pulled by owner ruling
    /// later that day — A is a per-screen option-row scheme button, and welding
    /// it to Enter double-fired scheme slots, e.g. Reset on the rebind screen.
    /// Enter is native-only now; A's slots stay reachable via number keys):
    ///   WASD  → left stick (the option funnel reads only the stick)
    ///   Backspace → B      U / I → X / Y
    ///   Q / E → LB / RB    Z / X → LT / RT
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
        // ---- Text-entry gate (game truth, memoized once per frame; all
        //      handles from the WP8 Seams registry) ----
        private static int _gateFrame = -1;
        private static bool _gateActive;

        /// <summary>True while the game itself is capturing typed text: the Tab
        /// console, the F2 feedback tool, or a text-entry popup (PopUpName /
        /// PopUpCreateSave / PopUpSaveRename — the three getInputString
        /// consumers; a game update adding a fourth is a WP8 seam-audit row).
        ///
        /// MUST NOT evaluate before the game is initialized: reading
        /// ConsoleControl.console runs ConsoleControl's static constructor,
        /// which reads GameData — touched at frame 0 that NREs on unloaded data
        /// and PERMANENTLY poisons the type, killing MainControl.Update every
        /// frame after (the 2026-08-16 black-screen boot). First state
        /// classification arrives strictly after "Application Ready!", and the
        /// game's own MainControl.Update initializes ConsoleControl in that same
        /// first ready frame — so past this guard the cctor has already run,
        /// safely, on the game's side.</summary>
        public static bool TextEntryActive()
        {
            if (GameStateTracker.CurrentMode == GameMode.Unknown) return false;
            if (Time.frameCount == _gateFrame) return _gateActive;
            _gateFrame = Time.frameCount;
            _gateActive = ComputeTextEntryActive();
            return _gateActive;
        }

        private static bool ComputeTextEntryActive()
        {
            try
            {
                if (Seams.ConsoleControl_console != null && (bool)Seams.ConsoleControl_console.GetValue(null)) return true;
                if (Seams.FeedbackTool_takeInput != null && (bool)Seams.FeedbackTool_takeInput.GetValue(null)) return true;
                if (Seams.PopUpControl_getCurrentPopUp != null)
                {
                    object popup = Seams.PopUpControl_getCurrentPopUp.Invoke(null, null);
                    if (popup != null)
                    {
                        Type t = popup.GetType();
                        if (t == Seams.PopUpNameType || t == Seams.PopUpCreateSaveType || t == Seams.PopUpSaveRenameType)
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

        // ---- Patch application (accessor handles from the Seams registry;
        //      a missing accessor costs that one button, already counted in
        //      the boot audit) ----

        public static void Apply(Harmony harmony)
        {
            if (Seams.ControllerInputControlType == null)
            {
                Plugin.Logger?.LogError("[Feed] SkaldIO+ControllerInputControl not found — keyboard feed unavailable");
                return;
            }

            int applied = 0;
            applied += Patch(harmony, "buttonBPressed", nameof(Postfix_ButtonB));
            applied += Patch(harmony, "buttonXPressed", nameof(Postfix_ButtonX));
            applied += Patch(harmony, "buttonYPressed", nameof(Postfix_ButtonY));
            applied += Patch(harmony, "leftBumperPressed", nameof(Postfix_LeftBumper));
            applied += Patch(harmony, "rightBumperPressed", nameof(Postfix_RightBumper));
            applied += Patch(harmony, "leftTriggerPressed", nameof(Postfix_LeftTriggerPressed));
            applied += Patch(harmony, "leftTriggerHeld", nameof(Postfix_LeftTriggerHeld));
            applied += Patch(harmony, "leftTriggerUp", nameof(Postfix_LeftTriggerUp));
            applied += Patch(harmony, "rightTriggerPressed", nameof(Postfix_RightTriggerPressed));
            applied += Patch(harmony, "rightTriggerHeld", nameof(Postfix_RightTriggerHeld));
            applied += Patch(harmony, "rightTriggerUp", nameof(Postfix_RightTriggerUp));
            applied += Patch(harmony, "isLeftStickUpPressed", nameof(Postfix_StickUpPressed));
            applied += Patch(harmony, "isLeftStickUpHeld", nameof(Postfix_StickUpHeld));
            applied += Patch(harmony, "isLeftStickDownPressed", nameof(Postfix_StickDownPressed));
            applied += Patch(harmony, "isLeftStickDownHeld", nameof(Postfix_StickDownHeld));
            applied += Patch(harmony, "isLeftStickLeftPressed", nameof(Postfix_StickLeftPressed));
            applied += Patch(harmony, "isLeftStickLeftHeld", nameof(Postfix_StickLeftHeld));
            applied += Patch(harmony, "isLeftStickRightPressed", nameof(Postfix_StickRightPressed));
            applied += Patch(harmony, "isLeftStickRightHeld", nameof(Postfix_StickRightHeld));
            Plugin.Logger?.LogInfo($"[Feed] Keyboard→controller feed live: {applied}/19 accessors");
        }

        private static int Patch(Harmony harmony, string methodName, string postfixName)
        {
            Seams.FeedAccessors.TryGetValue(methodName, out var method);
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

        // Activation-class emulations additionally suspend while the review
        // layer is open or eating its closing press (WP10) — a confirm from
        // inside the buffer must never fire. Stick emulation stays live:
        // navigation exits review, then acts.
        private static bool EmulateActivation(KeyCode key)
            => !ReviewLayer.EatingActivations() && Emulate(key);

        static void Postfix_ButtonB(ref bool __result)
        {
            if (__result) return;
            if (EmulateActivation(KeyCode.Backspace) || Time.frameCount == SkaldIOPatches.InjectCancelFrame) __result = true;
        }

        static void Postfix_ButtonX(ref bool __result) { if (!__result && EmulateActivation(KeyCode.U)) __result = true; }
        static void Postfix_ButtonY(ref bool __result) { if (!__result && EmulateActivation(KeyCode.I)) __result = true; }
        static void Postfix_LeftBumper(ref bool __result) { if (!__result && Emulate(KeyCode.Q)) __result = true; }
        static void Postfix_RightBumper(ref bool __result) { if (!__result && Emulate(KeyCode.E)) __result = true; }

        // Bridge confirm injects here as a synthetic LT click (press on frame N,
        // release on N+1 — the real click shape), matching the game's own
        // confirm idiom: LT clicks the focused element.
        static void Postfix_LeftTriggerPressed(ref bool __result)
        {
            if (__result) return;
            if (EmulateActivation(KeyCode.Z) || Time.frameCount == SkaldIOPatches.InjectConfirmFrame) __result = true;
        }
        static void Postfix_LeftTriggerHeld(ref bool __result)
        {
            if (__result) return;
            if ((!TextEntryActive() && !ReviewLayer.EatingActivations() && Input.GetKey(KeyCode.Z))
                || Time.frameCount == SkaldIOPatches.InjectConfirmFrame) __result = true;
        }
        static void Postfix_LeftTriggerUp(ref bool __result)
        {
            if (__result) return;
            if ((!TextEntryActive() && !ReviewLayer.EatingActivations() && Input.GetKeyUp(KeyCode.Z))
                || Time.frameCount == SkaldIOPatches.InjectConfirmFrame + 1) __result = true;
        }
        static void Postfix_RightTriggerPressed(ref bool __result) { if (!__result && EmulateActivation(KeyCode.X)) __result = true; }
        static void Postfix_RightTriggerHeld(ref bool __result) { if (!__result && !TextEntryActive() && !ReviewLayer.EatingActivations() && Input.GetKey(KeyCode.X)) __result = true; }
        static void Postfix_RightTriggerUp(ref bool __result) { if (!__result && !TextEntryActive() && !ReviewLayer.EatingActivations() && Input.GetKeyUp(KeyCode.X)) __result = true; }

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

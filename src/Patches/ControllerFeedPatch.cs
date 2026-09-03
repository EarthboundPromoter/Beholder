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
    ///   Q / E → LB / RB    Z / X → LT / RT
    ///
    /// R16 (owner ruling 2026-08-21, table-ui-design ledger): ALL face-button
    /// keyboard feeds unbound mod-wide — Backspace→B, U→X, I→Y retired. The
    /// 2026-08-21 consumer sweep proved every face-button function has a
    /// native keyboard twin: B is the Escape getter's controller twin
    /// (SkaldIO.cs:891-898), X/Y are the inventory/char-sheet quick-button
    /// aliases (covered by the live Inventory binding — default E, the
    /// owner's rebind "," — and C), and option-scheme slots are triple-pathed
    /// (mouse/numbers/controller) except the single AXBYButNoNumbers row
    /// (Craft/Clear), covered by the crafting chain's mouse delegation.
    /// Backspace, U, and I return to the free pool everywhere. The B accessor
    /// stays patched for the dev bridge's synthetic cancel ONLY (no keyboard
    /// emulation).
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
    /// THE PRESS LATCH (audit 2026-09-02, Shane's 0.5.6 log — "arrows take
    /// two or three presses" on menus, the feat tree, popups): the game
    /// polls every stick accessor from FixedUpdate (MainControl.cs:633-684:
    /// updateGameLogic → state.update() / popup handle() → SkaldIO.clear()),
    /// while a Unity key edge (Input.GetKeyDown) lasts exactly one RENDER
    /// frame. FixedUpdate runs 0..n times per render frame (fixed step 1/60
    /// s in the game data), so a raw GetKeyDown read AT POLL TIME is seen
    /// only when a tick happens to land in the key's frame: at Shane's
    /// ~150 fps roughly 40 % of presses, at the owner's 60 fps all of them
    /// (the symptom's exact good/bad split); below 60 fps the same read
    /// fires TWICE (two ticks in one frame — DialogueCursor.ActedThisFrame
    /// guarded that side locally). The game's own inputs never drop or
    /// double because SkaldIO.update (Update phase) LATCHES key-downs and
    /// stick edges and the tick's SkaldIO.clear() resets them. The feed now
    /// mirrors that exactly: Plugin.Update arms a per-key pending flag on
    /// the key-down frame (after the mod's own consumers ran), a tick-phase
    /// read (Time.inFixedTimeStep) answers the flag, and a postfix on
    /// SkaldIO.clear resets it at the tick boundary. Update-phase readers
    /// (GridNavigationPatch.Tick, SkaldIO.update's trigger merge) keep the
    /// one-frame GetKeyDown — a latch that outlives a frame would double
    /// them. Held reads are level-based and phase-safe as they were. One
    /// Info receipt per consumed press names the frames it waited, and the
    /// clock stamp counts ticks, so the next log states the cadence.
    ///
    /// Applied from SkaldIOPatches.ApplyPatches (deferred past SkaldIO's
    /// game-data-dependent static constructor).
    /// </summary>
    public static class ControllerFeedPatch
    {
        // ---- The press latch ----
        // Every key the feed emulates as a PRESS (edge) that a tick reads:
        // the four arrows and WASD (stick), Q/E (bumpers). Z/X are NOT here:
        // their only readers are the trigger merge inside SkaldIO.update
        // (Update phase, decomp SkaldIO.cs:520-540), so a latch for them
        // would only ever emit false "consumed by tick" receipts (review
        // finding 3). A tick-phase read of an unlatched key answers false,
        // never a raw edge — a new tick reader of Z/X would show up as a
        // dead key, not as a silent return of the defect.
        // (The two arrays below size themselves from LatchKeys and must stay
        // declared after it — static initializers run in textual order.)
        private static readonly KeyCode[] LatchKeys =
        {
            KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,
            KeyCode.W, KeyCode.A, KeyCode.S, KeyCode.D,
            KeyCode.Q, KeyCode.E,
        };
        private static readonly bool[] _pending = new bool[LatchKeys.Length];
        private static readonly int[] _armedFrame = new int[LatchKeys.Length];
        // Two flags (review finding 1 — the Mono-inline hazard): _latchArmed
        // says the SkaldIO.clear detour was APPLIED; _latchLive says it has
        // been OBSERVED running. Only the observed boundary is trusted —
        // a latch with no boundary would pin a key on forever, strictly
        // worse than the drop it replaces. Until the first tick confirms,
        // tick reads answer the raw edge (the pre-audit behaviour).
        private static bool _latchArmed;
        private static bool _latchLive;
        private static int _liveFrame = -1;     // the frame the boundary was first observed
        private static bool _staleReported;
        // A pending flag older than this many render frames means the
        // boundary stopped running: frames-per-tick is fps/60, so 120 empty
        // frames would need >7200 fps, and a loading stall advances neither
        // phase (catch-up ticks run before the resume frame's Update). The
        // one dependency: Time.timeScale never 0 — nothing in the game or
        // the mod writes timeScale (grep 2026-09-02); a future pause done
        // that way would have to suspend this sweep.
        private const int StaleFrames = 120;
        // Bridge one-shots expire this many frames after their armed frame
        // (review finding 7: under a grid the boundary leaves them alone, and
        // a popup over the grid would otherwise keep one armed for a phantom
        // press after the grid closes).
        private const int InjectExpiryFrames = 3;
        /// <summary>Game-logic ticks seen (one per SkaldIO.clear); the clock
        /// stamp prints the delta so a log states frames-per-tick, and the
        /// eat tails (EatTail) count on it.</summary>
        internal static long TickCount;
        /// <summary>True once the tick boundary has been observed running —
        /// the point from which TickCount is a trustworthy clock.</summary>
        internal static bool TickClockLive => _latchLive;

        private static int LatchIndex(KeyCode key)
        {
            for (int i = 0; i < LatchKeys.Length; i++) if (LatchKeys[i] == key) return i;
            return -1;
        }

        /// <summary>Plugin.Update, after InputHandler.ProcessInput: a key-down
        /// this render frame that no mod layer owns arms its flag. Gated at
        /// arm time as well as read time so a press the key table or a text
        /// entry consumed never survives into the next tick.</summary>
        internal static void ArmLatch()
        {
            if (!_latchArmed) return;
            int frame = Time.frameCount;
            // Stale sweep: a flag no tick boundary cleared for StaleFrames
            // is a broken boundary, not a press — drop it and say so, once,
            // at Error (the only runtime signal the boundary died; player
            // logs are Info-level).
            for (int i = 0; i < LatchKeys.Length; i++)
            {
                if (_pending[i] && frame - _armedFrame[i] > StaleFrames)
                {
                    _pending[i] = false;
                    if (!_staleReported)
                    {
                        _staleReported = true;
                        Plugin.Logger?.LogError($"[Feed] {LatchKeys[i]} pending since f{_armedFrame[i]} never cleared — SkaldIO.clear boundary not running? (reported once)");
                    }
                }
            }
            ExpireInjects(frame);
            // The flip frame (review pass 2, finding 1): the tick that
            // confirmed the boundary answered this frame's raw edge before
            // the latch was trusted — arming the same edge would fire it
            // again next tick.
            if (frame == _liveFrame) return;
            if (TextEntryActive() || KeyTable.Engaged) return;
            for (int i = 0; i < LatchKeys.Length; i++)
            {
                if (_pending[i] || !Input.GetKeyDown(LatchKeys[i])) continue;
                _pending[i] = true;
                _armedFrame[i] = frame;
            }
        }

        private static void ExpireInjects(int frame)
        {
            // Only under a selector grid, whose driver (GridNavigationPatch.
            // Tick) reads every frame — elsewhere the tick boundary owns the
            // injects, fps-independently; a frame-domain expiry there would
            // kill an inject before a tick at very high frame rates.
            if (!GridNavigationPatch.GridActive()) return;
            Expire(ref SkaldIOPatches.InjectUpFrame, frame);
            Expire(ref SkaldIOPatches.InjectDownFrame, frame);
            Expire(ref SkaldIOPatches.InjectLeftFrame, frame);
            Expire(ref SkaldIOPatches.InjectRightFrame, frame);
            Expire(ref SkaldIOPatches.InjectCancelFrame, frame);
            Expire(ref SkaldIOPatches.InjectNumericFrame, frame);
        }

        private static void Expire(ref int armedFrame, int frame)
        {
            if (armedFrame >= 0 && frame > armedFrame + InjectExpiryFrames) armedFrame = -1;
        }

        /// <summary>The edge read, phase-aware: inside the game's tick the
        /// latch answers (armed the frame the key went down, cleared when the
        /// tick ends); in the Update phase the raw one-frame edge answers.
        /// Without a live boundary the raw edge answers everywhere (the
        /// pre-audit behaviour, logged as an error at Apply).</summary>
        private static bool Pressed(KeyCode key)
        {
            if (_latchLive && Time.inFixedTimeStep)
            {
                int i = LatchIndex(key);
                return i >= 0 && _pending[i];
            }
            return Input.GetKeyDown(key);
        }

        /// <summary>Bridge one-shots (dev-only), phase-aware the same way. A
        /// tick read answers "armed" (field ≥ 0; the boundary clears it) —
        /// except while a selector grid is open, whose only driver is
        /// GridNavigationPatch.Tick in the Update phase: there the tick
        /// declines and the Update read consumes the one-shot itself once
        /// its frame has arrived (a tick precedes Update within a frame, so
        /// the boundary would otherwise eat the inject before the grid saw
        /// it). Without a live boundary the pre-audit frame-equality
        /// one-shot stands in both phases.</summary>
        private static bool Injected(ref int armedFrame)
        {
            if (armedFrame < 0) return false;
            if (!_latchLive) return Time.frameCount == armedFrame;
            if (Time.frameCount < armedFrame) return false;     // armed for a frame not yet reached
            if (Time.inFixedTimeStep) return !GridNavigationPatch.GridActive();
            // Update phase: only the selector grid's own driver
            // (GridNavigationPatch.Tick) may consume; every other Update-
            // phase reader (ScreenControl.updateResolution polls the B
            // accessor every frame — review finding 4) sees nothing.
            if (!GridNavigationPatch.GridActive()) return false;
            armedFrame = -1;
            return true;
        }

        /// <summary>SkaldIO.clear postfix — the end of every game-logic tick,
        /// where the game resets its own latches. Consumed presses log one
        /// Info receipt: the frame the key went down, the tick's frame, and
        /// the wait between them (0 never occurs — the tick precedes Update
        /// within a frame; 1 is the 60 fps norm; 2-3 is a 144+ Hz display).</summary>
        static void Postfix_Clear()
        {
            // The flags reset in `finally`, unconditionally and before any
            // logging can throw (review finding 2): a stranded flag is the
            // pinned-key runaway. Exceptions never leave the game's tick.
            try
            {
                TickCount++;
                int now = Time.frameCount;
                bool trusted = _latchLive;
                if (!trusted)
                {
                    // First boundary observed: the detour runs (not inlined
                    // into updateGameLogic) — from here tick reads trust the
                    // latch. Behavioural receipt, per the Mono-inline lesson.
                    _latchLive = true;
                    _liveFrame = now;
                    Plugin.Logger?.LogInfo($"[Feed] Press latch boundary confirmed (first SkaldIO.clear seen, f{now})");
                }
                // The receipt states the flag's LIFETIME (armed frame → the
                // boundary that cleared it), which is the audit's number. It
                // does not claim the game acted: TableCursor.ClaimsStick and
                // the grid's movement suppression zero reads before the
                // latch is consulted, and a cutscene tick reads nothing.
                for (int i = 0; i < LatchKeys.Length; i++)
                {
                    if (!_pending[i] || !trusted) continue;
                    Plugin.Logger?.LogInfo($"[Feed] [f{now}] {LatchKeys[i]} pressed f{_armedFrame[i]}, cleared at tick boundary +{now - _armedFrame[i]}f");
                }
                // Bridge one-shots that tick reads consume ride the same
                // boundary (confirm is an Update-phase read and keeps its
                // own frame; under a selector grid the Update-phase driver
                // consumes them itself, and the expiry sweep backstops).
                if (!GridNavigationPatch.GridActive())
                {
                    SkaldIOPatches.InjectUpFrame = SkaldIOPatches.InjectDownFrame = -1;
                    SkaldIOPatches.InjectLeftFrame = SkaldIOPatches.InjectRightFrame = -1;
                    SkaldIOPatches.InjectCancelFrame = -1;
                    SkaldIOPatches.InjectNumericFrame = -1;
                }
            }
            catch (Exception ex)
            {
                try { Plugin.Logger?.LogDebug($"[Feed:clear] {ex.Message}"); } catch { }
            }
            finally
            {
                Array.Clear(_pending, 0, _pending.Length);
            }
        }

        // ---- Text-entry gate (game truth, memoized once per frame; all
        //      handles from the WP8 Seams registry) ----
        private static long _gateKey = -1;
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
            if (Scaffold.TickClock.MemoKey == _gateKey) return _gateActive;   // tick OR frame change recomputes (audit 2026-09-03)
            _gateKey = Scaffold.TickClock.MemoKey;
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
            // R16: buttonXPressed / buttonYPressed deliberately NOT patched
            // (face-button feeds retired); buttonBPressed carries only the
            // bridge's synthetic cancel.
            applied += Patch(harmony, "buttonBPressed", nameof(Postfix_ButtonB));
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
            Plugin.Logger?.LogInfo($"[Feed] Keyboard→controller feed live: {applied}/17 accessors (X/Y retired, R16)");

            // The press latch's tick boundary: SkaldIO.clear ends every
            // game-logic tick (MainControl.updateGameLogic's last statement,
            // its only caller). A large enough body to be called, not
            // inlined; the receipts below are the behavioural proof.
            if (Seams.SkaldIO_clear != null)
            {
                harmony.Patch(Seams.SkaldIO_clear, postfix: new HarmonyMethod(typeof(ControllerFeedPatch), nameof(Postfix_Clear)));
                _latchArmed = true;   // trusted only once Postfix_Clear is observed (_latchLive)
                float step = Time.fixedDeltaTime;
                Plugin.Logger?.LogInfo($"[Feed] Press latch armed (Update arms, SkaldIO.clear resets; awaiting first tick); "
                    + $"tick {(step > 0f ? 1f / step : 0f):F0} Hz (fixedDeltaTime {step:F4}) "
                    + $"vsync={QualitySettings.vSyncCount} targetFps={Application.targetFrameRate} "
                    + $"display={Screen.currentResolution.refreshRate} Hz");
            }
            else
            {
                Plugin.Logger?.LogError("[Feed] SkaldIO.clear seam missing — press latch unavailable, "
                    + "tick reads fall back to raw key edges (presses drop above 60 fps)");
            }
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
            => !TextEntryActive() && !KeyTable.Engaged && Pressed(key);

        // Activation-class emulations additionally suspend while the review
        // layer is open or eating its closing press (WP10) — a confirm from
        // inside the buffer must never fire. Stick emulation stays live:
        // navigation exits review, then acts.
        private static bool EmulateActivation(KeyCode key)
            => !ReviewLayer.EatingActivations() && Emulate(key);

        /// <summary>R16: no keyboard emulation — only the dev bridge's
        /// synthetic cancel rides B now.</summary>
        static void Postfix_ButtonB(ref bool __result)
        {
            if (__result) return;
            // Tick-only: B has no Update-phase mod driver, and
            // ScreenControl.updateResolution polls the Escape getter every
            // Update frame — it must never consume the cancel (pass-2 find 3).
            if (Time.inFixedTimeStep && Injected(ref SkaldIOPatches.InjectCancelFrame)) __result = true;
        }

        static void Postfix_LeftBumper(ref bool __result) { if (!__result && Emulate(KeyCode.Q)) __result = true; }
        static void Postfix_RightBumper(ref bool __result) { if (!__result && Emulate(KeyCode.E)) __result = true; }

        // Bridge confirm injects here as a synthetic LT click (press on frame N,
        // release on N+1 — the real click shape), matching the game's own
        // confirm idiom: LT clicks the focused element.
        // InjectConfirmFrame keeps raw frame equality (deliberately outside
        // Injected and both sweeps): its only reader is SkaldIO.update's
        // trigger merge in the Update phase, where a one-frame one-shot is
        // exactly right and self-expiring.
        static void Postfix_LeftTriggerPressed(ref bool __result)
        {
            if (__result) return;
            if (EmulateActivation(KeyCode.Z) || Time.frameCount == SkaldIOPatches.InjectConfirmFrame) __result = true;
        }
        static void Postfix_LeftTriggerHeld(ref bool __result)
        {
            if (__result) return;
            if ((!TextEntryActive() && !KeyTable.Engaged && !ReviewLayer.EatingActivations() && Input.GetKey(KeyCode.Z))
                || Time.frameCount == SkaldIOPatches.InjectConfirmFrame) __result = true;
        }
        static void Postfix_LeftTriggerUp(ref bool __result)
        {
            if (__result) return;
            if ((!TextEntryActive() && !KeyTable.Engaged && !ReviewLayer.EatingActivations() && Input.GetKeyUp(KeyCode.Z))
                || Time.frameCount == SkaldIOPatches.InjectConfirmFrame + 1) __result = true;
        }
        static void Postfix_RightTriggerPressed(ref bool __result) { if (!__result && EmulateActivation(KeyCode.X)) __result = true; }
        static void Postfix_RightTriggerHeld(ref bool __result) { if (!__result && !TextEntryActive() && !KeyTable.Engaged && !ReviewLayer.EatingActivations() && Input.GetKey(KeyCode.X)) __result = true; }
        static void Postfix_RightTriggerUp(ref bool __result) { if (!__result && !TextEntryActive() && !KeyTable.Engaged && !ReviewLayer.EatingActivations() && Input.GetKeyUp(KeyCode.X)) __result = true; }

        // The stick postfixes also carry the player-nav stamp (owner ruling
        // 2026-08-17): a direction read answering true IS the player
        // navigating — real key, stick, or bridge drive. The stamp lives HERE,
        // on the already-detoured inner accessors, because detouring the
        // one-line SkaldIO wrappers instead broke the keyboard outright:
        // Harmony's stub for a patched wrapper is a fresh JIT that inlines the
        // inner's PRISTINE IL, bypassing these postfixes (a8de251-class Mono
        // lesson, second occurrence — owner-caught regression, same day).
        // Confirms deliberately never stamp: they mount surfaces whose
        // same-frame init writes must stay queued behind the entry read.
        // TP2: the dialogue cursor claims stick presses in scene states from
        // HERE — the already-detoured layer (never the one-line SkaldIO
        // wrappers, the Mono-inline lesson). A claimed press acted (walked the
        // text / hopped a topic, with its own player-nav stamp) and returns
        // the read to false so the option funnel never sees it.
        // Nav revision §6, widened by the stick-consumer survey (owner
        // fixups 2026-08-23): the stick emulation reads ARROWS on every
        // ELEMENT surface and WASD only where the stick is WORLD movement.
        // The survey enumerated every native stick consumer: WORLD =
        // OverlandState walking, CombatPlanningState stepping,
        // CombatPlacementState cursor; ELEMENT = selector grids, ALL popups
        // (PopUpBase.updateControllerScrolling — the yes/no class),
        // scene/dialogue option funnels (SceneBaseState — DialogueCursor's
        // claim is key-agnostic, it consumes the boolean), the sheet/feat
        // families (InfoBaseState — closes the FEAT TREE registry gap), the
        // creation family (CharacterBuilderBaseState — stats/feats
        // editors), and the menu family (BaseMenuState — credits, intro,
        // nested). TableCursor-owned states are inert overlaps: ClaimsStick
        // zeroes the read before FeedKey is consulted. Popup-vs-state is
        // race-free by the game's own strict if/else (MainControl.cs:118).
        // The K-latch stick claim is RETIRED (nav revision §5: WASD
        // released to native character stepping under the latch).
        private static long _elemKey = -1;
        private static bool _elemCache;
        private static bool ElementSurface()
        {
            if (Scaffold.TickClock.MemoKey == _elemKey) return _elemCache;   // tick OR frame change recomputes (audit 2026-09-03)
            _elemKey = Scaffold.TickClock.MemoKey;
            bool elem = false;
            try
            {
                if (GridNavigationPatch.GridActive()) elem = true;
                else if (Seams.PopUpControl_getCurrentPopUp?.Invoke(null, null) != null) elem = true;
                else
                {
                    object state = Pump.CurrentStateObject();
                    elem = state != null
                        && ((Seams.SceneBaseStateType?.IsInstanceOfType(state) ?? false)
                         || (Seams.InfoBaseStateType?.IsInstanceOfType(state) ?? false)
                         || (Seams.CharacterBuilderBaseStateType?.IsInstanceOfType(state) ?? false)
                         || (Seams.BaseMenuStateType?.IsInstanceOfType(state) ?? false));
                }
            }
            catch { elem = false; }
            _elemCache = elem;
            return elem;
        }

        private static KeyCode FeedKey(KeyCode wasd, KeyCode arrow)
            => ElementSurface() ? arrow : wasd;

        static void Postfix_StickUpPressed(ref bool __result)
        {
            if (TableCursor.ClaimsStick) { __result = false; return; }
            if (!__result && (Emulate(FeedKey(KeyCode.W, KeyCode.UpArrow)) || Injected(ref SkaldIOPatches.InjectUpFrame))) __result = true;
            if (__result && DialogueCursor.ClaimStickUp()) { __result = false; return; }
            if (__result) Pump.NotePlayerNav();
        }
        static void Postfix_StickDownPressed(ref bool __result)
        {
            if (TableCursor.ClaimsStick) { __result = false; return; }
            if (!__result && (Emulate(FeedKey(KeyCode.S, KeyCode.DownArrow)) || Injected(ref SkaldIOPatches.InjectDownFrame))) __result = true;
            if (__result && DialogueCursor.ClaimStickDown()) { __result = false; return; }
            if (__result) Pump.NotePlayerNav();
        }
        // NOTE (ride 2026-08-18): the A/D topic-hop claim was wired here and
        // NEVER FIRED — no scene state reads the sideways accessors, so these
        // postfixes don't run in scenes at all (the same decomp fact that made
        // the claim "safe" made it dead: a read-choke claim only fires when
        // someone reads). PARKED at owner direction; the revival path is
        // mod-side Update input handling (InputHandler) with the same gate,
        // not an accessor postfix.
        static void Postfix_StickLeftPressed(ref bool __result)
        {
            if (TableCursor.ClaimsStick) { __result = false; return; }
            if (!__result && (Emulate(FeedKey(KeyCode.A, KeyCode.LeftArrow)) || Injected(ref SkaldIOPatches.InjectLeftFrame))) __result = true;
            if (__result) Pump.NotePlayerNav();
        }
        static void Postfix_StickRightPressed(ref bool __result)
        {
            if (TableCursor.ClaimsStick) { __result = false; return; }
            if (!__result && (Emulate(FeedKey(KeyCode.D, KeyCode.RightArrow)) || Injected(ref SkaldIOPatches.InjectRightFrame))) __result = true;
            if (__result) Pump.NotePlayerNav();
        }
        // Held reads stay raw Input.GetKey at tick time: level-based, so
        // phase-safe. One known divergence from native (review note 8): the
        // game's InputValue holds a RELEASE until the press is consumed, so
        // a native tap shows pressed AND held in the consuming tick; a
        // keyboard tap released before its tick shows pressed only. No
        // consumer pairs the two reads today — revisit if one appears.
        static void Postfix_StickUpHeld(ref bool __result) { if (TableCursor.ClaimsStick) { __result = false; return; } if (!__result && !TextEntryActive() && !KeyTable.Engaged && Input.GetKey(FeedKey(KeyCode.W, KeyCode.UpArrow))) __result = true; if (__result) Pump.NotePlayerNav(); }
        static void Postfix_StickDownHeld(ref bool __result) { if (TableCursor.ClaimsStick) { __result = false; return; } if (!__result && !TextEntryActive() && !KeyTable.Engaged && Input.GetKey(FeedKey(KeyCode.S, KeyCode.DownArrow))) __result = true; if (__result) Pump.NotePlayerNav(); }
        static void Postfix_StickLeftHeld(ref bool __result) { if (TableCursor.ClaimsStick) { __result = false; return; } if (!__result && !TextEntryActive() && !KeyTable.Engaged && Input.GetKey(FeedKey(KeyCode.A, KeyCode.LeftArrow))) __result = true; if (__result) Pump.NotePlayerNav(); }
        static void Postfix_StickRightHeld(ref bool __result) { if (TableCursor.ClaimsStick) { __result = false; return; } if (!__result && !TextEntryActive() && !KeyTable.Engaged && Input.GetKey(FeedKey(KeyCode.D, KeyCode.RightArrow))) __result = true; if (__result) Pump.NotePlayerNav(); }
    }
}

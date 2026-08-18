using HarmonyLib;
using UnityEngine;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// The mouse guard (owner ruling 2026-08-17). The game stores the virtual
    /// cursor as OFFSETS from the physical pointer and zeroes both the moment
    /// the physical mouse moves a single pixel (SkaldIO.updateMousePosition,
    /// decomp lines 439-443); right-stick drift additionally feeds the offsets
    /// every tick. Every keyboard snap in the game therefore survives only
    /// until desk-bump jitter or stick noise — fatal for single-shot snaps
    /// (the CC attribute editor's plus/minus flip snap was the type case).
    /// WP11's overland latch solved this for one surface; this is the same
    /// idea at the mechanism class:
    ///
    ///  - Latch: postfix on setVirtualMousePosition records the target —
    ///    every keyboard/controller snap, game-side or mod-side, flows
    ///    through this one setter.
    ///  - Re-assert: postfix on updateMousePosition (after the game's merge,
    ///    before hover processing) re-calls the game's own setter each tick,
    ///    wiping jitter zero-outs and stick drift alike.
    ///  - Release: net physical displacement since latch beyond ~2 game
    ///    pixels (scale-aware) means the player took the mouse — the guard
    ///    stands down until the next snap. Deliberate takeover costs one
    ///    small move; jitter oscillates below the threshold indefinitely.
    ///  - The WP11 cursor latch owns the mouse while holding; the guard
    ///    defers. State transitions drop the latch (entry snaps re-latch).
    ///
    /// Config docket (build-plan, glyph-config family): physical-controller
    /// players steering with the right stick would fight the latch — the
    /// future controller-mode config disables the guard.
    /// Applied from the DEFERRED batch (SkaldIO detours force the cctor at
    /// frame 0 — the WP7/WP9 boot-kill class). Seam-gated (WP8).
    /// </summary>
    public static class MouseGuardPatch
    {
        private static bool _latched;
        private static int _latchX, _latchY;        // game-space target
        private static Vector2 _latchPhysical;      // Input.mousePosition at latch
        private static bool _reasserting;           // our own re-assert must not re-record

        internal static void Apply(Harmony harmony)
        {
            if (Seams.SkaldIO_setVirtualMousePosition == null || Seams.SkaldIO_updateMousePosition == null)
            {
                Plugin.Logger?.LogError("[MouseGuard] seams missing — keyboard snaps stay jitter-fragile");
                return;
            }
            harmony.Patch(Seams.SkaldIO_setVirtualMousePosition,
                postfix: new HarmonyMethod(typeof(MouseGuardPatch), nameof(Postfix_SetVirtual)));
            harmony.Patch(Seams.SkaldIO_updateMousePosition,
                postfix: new HarmonyMethod(typeof(MouseGuardPatch), nameof(Postfix_UpdateMouse)));
            Plugin.Logger?.LogInfo("[MouseGuard] Snap latch armed (setVirtualMousePosition + updateMousePosition)");
        }

        internal static void OnStateTransition()
        {
            _latched = false;
        }

        static void Postfix_SetVirtual(int x, int y)
        {
            if (_reasserting) return;
            _latched = true;
            _latchX = x;
            _latchY = y;
            _latchPhysical = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            // CP4 yield discipline: a deliberate game-side placement (Ctrl
            // row snap, popup snap, funnel snap) releases the combat cursor's
            // hold exactly like a physical takeover — the latch never fights
            // the game. The cursor's own asserts are flagged and skip this.
            CombatCursor.NoteExternalMouseSet();
        }

        static void Postfix_UpdateMouse()
        {
            if (!_latched) return;
            if (OverlandCursor.HoldsMouse) return;
            if (CombatCursor.HoldsMouse) return;   // CP3 peer (survey §6 ⑥)

            float dx = Input.mousePosition.x - _latchPhysical.x;
            float dy = Input.mousePosition.y - _latchPhysical.y;
            float threshold = 2f * (Screen.width / 480f);
            if (dx * dx + dy * dy > threshold * threshold)
            {
                _latched = false; // the player took the mouse
                return;
            }
            try
            {
                _reasserting = true;
                Seams.SkaldIO_setVirtualMousePosition.Invoke(null, new object[] { _latchX, _latchY });
            }
            catch { }
            finally { _reasserting = false; }
        }
    }
}

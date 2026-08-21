using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// The scrollbar arrow reclaim (table-UI foundation, verification-gate
    /// item 1; docs\table-ui-design.md §9 patch 1). Scrollbars read raw held
    /// arrows, hardcoded, gated on hovering the target canvas
    /// (UIScrollbar.updateMouseInteraction:122-128 via SkaldIO.getButtonScrollUp/
    /// Down, SkaldIO.cs:1091-1099) — i.e. exactly where the future row cursor
    /// parks. Reclaimed so held arrows never scroll a hovered canvas out from
    /// under the cursor; mouse wheel, click-scroll, and drag stay native.
    ///
    /// Two detour points, receipt-arbitrated (the Mono inline hazard rule):
    ///  1. Skip-prefix on the two getters — tiny methods, MAY be inlined into
    ///     their caller; a first-consult [Gate] line proves the detour live.
    ///  2. Backstop on UIScrollbar.updateMouseInteraction — VIRTUAL, so it is
    ///     called through the vtable and can never be inline-bypassed. Prefix
    ///     captures the scroll degree; postfix reverts an increment-shaped
    ///     change made while an arrow is held and no wheel/click/drag input
    ///     explains it. If the backstop ever fires, the getter choke was
    ///     inlined — the [Gate] line says so, and the receipt records that the
    ///     fallback is the load-bearing layer.
    ///
    /// Applied from the DEFERRED batch (SkaldIO detours force the cctor at
    /// frame 0 — the WP7/WP9 boot-kill class). Seam-gated (WP8).
    /// </summary>
    public static class ScrollbarReclaimPatch
    {
        private static ConfigEntry<bool> _cfgReclaim;
        private static bool _consultLogged;
        private static bool _backstopLogged;

        internal static void BindConfig(ConfigFile config)
        {
            _cfgReclaim = config.Bind("Tables", "ScrollbarArrowReclaim", true,
                "Reclaim held arrow keys from hovered-canvas scrollbar scrolling (table-UI gate item 1). Off restores the game's arrow-scroll.");
        }

        private static bool Active => _cfgReclaim == null || _cfgReclaim.Value;

        internal static void Apply(Harmony harmony)
        {
            int applied = 0;
            if (Seams.SkaldIO_getButtonScrollUp != null)
            {
                harmony.Patch(Seams.SkaldIO_getButtonScrollUp,
                    prefix: new HarmonyMethod(typeof(ScrollbarReclaimPatch), nameof(Prefix_ButtonScroll)));
                applied++;
            }
            if (Seams.SkaldIO_getButtonScrollDown != null)
            {
                harmony.Patch(Seams.SkaldIO_getButtonScrollDown,
                    prefix: new HarmonyMethod(typeof(ScrollbarReclaimPatch), nameof(Prefix_ButtonScroll)));
                applied++;
            }
            if (Seams.UIScrollbar_updateMouseInteraction != null
                && Seams.UIScrollbar_degree != null && Seams.UIScrollbar_increment != null)
            {
                harmony.Patch(Seams.UIScrollbar_updateMouseInteraction,
                    prefix: new HarmonyMethod(typeof(ScrollbarReclaimPatch), nameof(Prefix_CaptureDegree)),
                    postfix: new HarmonyMethod(typeof(ScrollbarReclaimPatch), nameof(Postfix_RevertArrowScroll)));
                applied++;
            }
            if (applied == 3)
                Plugin.Logger?.LogInfo("[Tables] Scrollbar arrow reclaim armed (getter choke + virtual backstop)");
            else
                Plugin.Logger?.LogError($"[Tables] Scrollbar reclaim incomplete ({applied}/3 targets) — see seam report");
        }

        /// <summary>Credits exemption (Sonnet SHOULD-FIX 2026-08-21):
        /// UIScrollbarCredits.canMouseWheelScroll is always-true — the credits
        /// screen was never hover-gated, so the reclaim's rationale does not
        /// apply and held arrows are its only keyboard scroll. The flag is set
        /// by the owning updateMouseInteraction prefix before the getters run
        /// within that same call (their single call site, sweep-verified), so
        /// it is always fresh.</summary>
        private static bool _exemptScrollbar;

        static bool Prefix_ButtonScroll(ref bool __result)
        {
            if (!Active || _exemptScrollbar) return true;
            if (!_consultLogged)
            {
                _consultLogged = true;
                Scaffold.Log.Debug("Gate", "scrollbar getter choke consulted — detour live, not inlined (receipt 1)");
            }
            __result = false;
            return false;
        }

        static void Prefix_CaptureDegree(object __instance, out float __state)
        {
            __state = float.NaN;
            if (!Active) return;
            _exemptScrollbar = __instance != null && __instance.GetType().Name == "UIScrollbarCredits";
            if (_exemptScrollbar) return;
            // Perf gate (Sonnet note): capture only when an arrow is actually
            // held — the revert can only be owed then.
            if (!Input.GetKey(KeyCode.UpArrow) && !Input.GetKey(KeyCode.DownArrow)) return;
            try { __state = (float)Seams.UIScrollbar_degree.GetValue(__instance); }
            catch { }
        }

        static void Postfix_RevertArrowScroll(object __instance, float __state)
        {
            if (!Active || float.IsNaN(__state)) return;
            try
            {
                if (!Input.GetKey(KeyCode.UpArrow) && !Input.GetKey(KeyCode.DownArrow)) return;
                float now = (float)Seams.UIScrollbar_degree.GetValue(__instance);
                if (now == __state) return;

                // A legitimate same-frame scroll excludes the revert: wheel
                // paths and the button-click paths (which need a left-up).
                if (InvokeBool(Seams.SkaldIO_getMouseWheelScrollUp)
                    || InvokeBool(Seams.SkaldIO_getMouseWheelScrollDown)) return;
                try
                {
                    if (Seams.SkaldIO_getMouseUp != null
                        && (bool)Seams.SkaldIO_getMouseUp.Invoke(null, new object[] { 0 })) return;
                }
                catch { }

                // Only an increment-shaped move is the arrow/wheel/click family;
                // a drag writes arbitrary degrees and is left alone. (At the
                // clamp boundary a partial step escapes this test — harmless,
                // and only reachable at all if the getter choke was inlined.)
                int increment = 1;
                try { increment = (int)Seams.UIScrollbar_increment.GetValue(__instance); } catch { }
                if (increment <= 1) return;
                float step = 1f / increment;
                if (Mathf.Abs(Mathf.Abs(now - __state) - step) > step * 0.01f) return;

                Seams.UIScrollbar_degree.SetValue(__instance, __state);
                if (!_backstopLogged)
                {
                    _backstopLogged = true;
                    Scaffold.Log.Debug("Gate",
                        "scrollbar BACKSTOP reverted an arrow scroll — getter choke inlined; the virtual-method layer is load-bearing (receipt 1)");
                }
            }
            catch { }
        }

        private static bool InvokeBool(System.Reflection.MethodInfo m)
        {
            try { return m != null && (bool)m.Invoke(null, null); }
            catch { return false; }
        }
    }
}

using HarmonyLib;
using System;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// CP3: the combat cursor's game-side prefixes (survey §6 ⑦) — a lingering
    /// inspect tooltip under the virtual mouse would eat the first Z after an
    /// X-inspect, so the combat states' own mouse branches clear it while the
    /// cursor holds the mouse (the exact WP11 discipline, combat edition —
    /// planning, placement, and targeting each own a setMousePosition).
    /// Applied from the deferred batch (game-type detours). Seam-gated (WP8);
    /// the targets are the states' full mouse-processing bodies, far outside
    /// the Mono inline hazard class.
    /// </summary>
    internal static class CombatCursorPatches
    {
        internal static void Apply(Harmony harmony)
        {
            int applied = 0;
            foreach (var target in new[]
            {
                Seams.CombatPlanning_setMousePosition,
                Seams.CombatPlacement_setMousePosition,
                Seams.CombatTargeting_setMousePosition,
            })
            {
                if (target == null) continue;
                try
                {
                    harmony.Patch(target,
                        prefix: new HarmonyMethod(typeof(CombatCursorPatches), nameof(Prefix_ClearTooltip)));
                    applied++;
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogError($"[CombatCursor] mouse-branch patch failed: {ex.Message}");
                }
            }
            if (applied == 3)
                Plugin.Logger?.LogInfo("[CombatCursor] Tooltip-clear prefixes live (planning/placement/targeting)");
            else
                Plugin.Logger?.LogError($"[CombatCursor] Tooltip-clear incomplete ({applied}/3)");
        }

        static void Prefix_ClearTooltip()
        {
            try
            {
                if (!CombatCursor.HoldsMouse) return;
                if (Seams.ToolTipPrinter_hasToolTip != null
                    && (bool)Seams.ToolTipPrinter_hasToolTip.Invoke(null, null))
                    Seams.ToolTipPrinter_clearToolTip?.Invoke(null, null);
            }
            catch { }
        }
    }
}

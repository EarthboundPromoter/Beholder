using HarmonyLib;
using System;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// T light toggle spoken (owner request 2026-08-29): Inventory.toggleLight
    /// is the one choke — overland T and combat-planning T both route here via
    /// Party.toggleLightOnOff. Lit/doused is the game's own authoritative flag
    /// (getCurrentLight != null) read before and after; a no-op press (no
    /// lantern in the party) stays the game's own popup voice. The line names
    /// the actual light item ("Lantern lit." / "Torch doused.").
    /// </summary>
    [HarmonyPatch(typeof(Inventory), "toggleLight")]
    public static class LightTogglePatch
    {
        internal struct LightState
        {
            public bool Lit;
            public string Name;
        }

        static void Prefix(Character user, out LightState __state)
        {
            __state = default;
            try
            {
                var light = user?.getCurrentLight();
                __state.Lit = light != null;
                __state.Name = light == null ? null : TextCleaner.CleanText(light.getName());
            }
            catch { }
        }

        static void Postfix(Character user, LightState __state)
        {
            try
            {
                var light = user?.getCurrentLight();
                bool lit = light != null;
                if (lit == __state.Lit) return; // no-op press — the game's popup speaks

                string name = lit ? TextCleaner.CleanText(light.getName()) : __state.Name;
                if (string.IsNullOrWhiteSpace(name)) name = "Light";
                Scaffold.SpeechService.SayQueued(lit ? $"{name} lit." : $"{name} doused.", "Light");
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("LightToggle", ex.Message);
            }
        }
    }

    /// <summary>
    /// Stealth toggle (owner request 2026-08-29): DataControl.hide toggles the
    /// current PC's hidden flag then spends a round via passRound, whose
    /// generic "You wait a short while." strip line is what used to speak.
    /// The note lets the Pump swap exactly that line for "Entered stealth." /
    /// "Left stealth." from the game's own isHidden flag; genuine waits keep
    /// their line.
    /// </summary>
    [HarmonyPatch(typeof(DataControl), "hide")]
    public static class HidePatch
    {
        static void Postfix(DataControl __instance)
        {
            try
            {
                var pc = __instance.getCurrentPC();
                if (pc == null) return;
                // Spoken here, from the game's own flag — never anchored to
                // the strip line, whose dedup can swallow a repeat (the
                // owner-caught "left stealth goes quiet" bug). The note only
                // drops that misleading wait line at the drain.
                Scaffold.SpeechService.SayQueued(
                    pc.isHidden() ? "Entered stealth." : "Left stealth.", "Stealth");
                Pump.NoteStealthToggle();
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("Hide", ex.Message);
            }
        }
    }

    /// <summary>
    /// Character-change vitals (owner request 2026-08-29): DataControl.changePC
    /// is the one choke for the Next Character cycle (StateBase quick-button,
    /// the popup carousel path). The line — "8 of 10 vitality" + wounds and
    /// conditions only when nonzero (owner: zero conditions, no string at
    /// all) — notes at the Pump and drains right after DrainContent, queuing
    /// behind the game's own "X is now leading the party." strip line.
    /// index == 0 mirrors the game's own no-change guard.
    /// </summary>
    [HarmonyPatch(typeof(DataControl), "changePC")]
    public static class ChangePCPatch
    {
        static void Postfix(DataControl __instance, int index)
        {
            try
            {
                if (index == 0) return;
                object pc = __instance.getCurrentPC();
                if (pc == null) return;
                string line = ComposeVitals(pc);
                if (line != null) Pump.NotePCVitals(line);
            }
            catch (Exception ex)
            {
                Scaffold.Log.Throttled("ChangePC", ex.Message);
            }
        }

        private static string ComposeVitals(object ch)
        {
            int vit = I(Seams.Character_getVitality, ch);
            if (vit < 0) return null;
            int maxVit = I(Seams.Character_getMaxVitality, ch);
            string line = maxVit > 0 ? $"{vit} of {maxVit} vitality" : $"{vit} vitality";

            int wounds = I(Seams.Character_getWounds, ch);
            if (wounds > 0) line += $", {wounds} {(wounds == 1 ? "wound" : "wounds")}";

            int cond = CombatantDocument.ConditionCount(ch);
            if (cond > 0) line += $", {cond} {(cond == 1 ? "condition" : "conditions")}";

            return line + ".";
        }

        private static int I(System.Reflection.MethodInfo m, object ch)
        {
            try { return m == null ? -1 : (int)m.Invoke(ch, null); }
            catch { return -1; }
        }
    }
}

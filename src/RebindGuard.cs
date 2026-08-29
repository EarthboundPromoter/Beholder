using UnityEngine;

namespace SkaldAccessibility
{
    /// <summary>
    /// Silent auto-rebind (owner ruling 2026-08-29, replacing the manual
    /// first-run setup): the mod's keymap assumes Next Character on Period and
    /// Inventory on Comma — the game's Q/E defaults collide with the mod's
    /// bumper feed (Q=LB, E=RB). Checked on every state transition against the
    /// LIVE SkaldIO.keyBindings instance (settingsLoad replaces it wholesale
    /// at boot, so a per-transition check is self-healing whichever side of
    /// the load it first runs); a mismatch is reasserted with the game's own
    /// updateKey collision rule — the wanted key is first cleared from any
    /// binding holding it — then persisted through the game's own
    /// settingsSave. Covers first launch, the rebind screen's Reset, manual
    /// changes, and a wiped settings file. No speech — the readme carries the
    /// disclosure; config Input.AutoRebind (default on) opts out.
    /// </summary>
    public static class RebindGuard
    {
        private static BepInEx.Configuration.ConfigEntry<bool> _enabled;

        public static void BindConfig(BepInEx.Configuration.ConfigFile config)
        {
            _enabled = config.Bind("Input", "AutoRebind", true,
                "Silently keep the game's Next Character binding on Period and Inventory on Comma "
                + "(their Q/E defaults collide with the mod's shoulder-button keys). "
                + "Turn off to manage these two bindings yourself.");
        }

        /// <summary>Called from GameStateTracker.OnStateChanged — every state
        /// transition, so an undone rebind never survives past the next screen
        /// change.</summary>
        public static void Check()
        {
            if (_enabled != null && !_enabled.Value) return;
            if (Seams.KeyBinding_setKey == null || Seams.KeyBinding_clearIfKey == null
                || Seams.KeyBindings_nextCharacter == null || Seams.KeyBindings_inventory == null
                || Seams.SkaldObjectList_objectList == null) return;

            try
            {
                var kb = SkaldIO.keyBindings;
                if (kb == null) return;

                bool changed = false;
                if (kb.getNextCharacterKey() != KeyCode.Period)
                    changed |= Assert(kb, Seams.KeyBindings_nextCharacter, KeyCode.Period);
                if (kb.getInventoryKey() != KeyCode.Comma)
                    changed |= Assert(kb, Seams.KeyBindings_inventory, KeyCode.Comma);

                if (changed)
                {
                    Plugin.Logger?.LogInfo(
                        "[Rebind] Standing rebinds reasserted: Next Character -> Period, Inventory -> Comma.");
                    try { MainControl.getDataControl()?.settingsSave(); }
                    catch (System.Exception ex)
                    { Scaffold.Log.Throttled("Rebind:save", ex.Message); }
                }
            }
            catch (System.Exception ex)
            {
                Scaffold.Log.Throttled("Rebind:check", ex.Message);
            }
        }

        private static bool Assert(SKALDKeyBindings kb, System.Reflection.FieldInfo slot, KeyCode want)
        {
            // The game's own updateKey rule first: any binding holding the
            // wanted key loses it, so a key never means two things.
            var list = Seams.SkaldObjectList_objectList.GetValue(kb) as System.Collections.IEnumerable;
            if (list != null)
                foreach (object binding in list)
                    Seams.KeyBinding_clearIfKey.Invoke(binding, new object[] { want });

            object target = slot.GetValue(kb);
            if (target == null) return false;
            Seams.KeyBinding_setKey.Invoke(target, new object[] { want });
            return true;
        }
    }
}

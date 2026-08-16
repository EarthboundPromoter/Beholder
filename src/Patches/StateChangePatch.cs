using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Note-only postfix on MainControl+StateControl.setState(SkaldStates) — the
    /// game's own state-machine entry point, called every frame from
    /// StateControl.update() (decompiled MainControl.cs:117/212). The postfix
    /// records the StateControl instance and nothing else; the Pump's drain reads
    /// currentState and diffs at end of frame. Replaces the WP3-retired PollState
    /// poller (build-plan WP3).
    /// </summary>
    [HarmonyPatch]
    public static class StateChangePatch
    {
        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("MainControl+StateControl");
            if (type == null)
            {
                Plugin.Logger?.LogError("[StateChange] MainControl+StateControl not found");
                return null;
            }
            var method = AccessTools.Method(type, "setState");
            if (method == null)
            {
                Plugin.Logger?.LogError("[StateChange] setState not found");
                return null;
            }
            Plugin.Logger?.LogInfo("[StateChange] Patching StateControl.setState");
            return method;
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            Pump.NoteStateControl(__instance);
        }
    }
}

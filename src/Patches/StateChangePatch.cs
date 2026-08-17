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
    /// poller (build-plan WP3). Seam-gated (WP8).
    /// </summary>
    [HarmonyPatch]
    public static class StateChangePatch
    {
        [HarmonyPrepare]
        static bool Prepare() => Seams.StateControl_setState != null;

        [HarmonyTargetMethod]
        static MethodBase TargetMethod() => Seams.StateControl_setState;

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            Pump.NoteStateControl(__instance);
        }
    }
}

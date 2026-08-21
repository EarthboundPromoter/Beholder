using HarmonyLib;
using System.Reflection;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Party-management move receipts (table gate F, §6.13 — "Kat to Camp
    /// Followers. Main Party, 5."). The two DataControl move verbs are the
    /// event chokes; speech fires only on a REAL move (both counts diffed
    /// across the call — every refusal is the game's own PopUpOK and rides
    /// PopupAnnouncePatch), and only while the party-management screen is
    /// up (the verbs are reachable from scripts). Seam-gated (WP8).
    /// </summary>
    public static class PartyMovePatch
    {
        private static bool Ready =>
            Seams.DataControl_sendCharacterToBench != null
            && Seams.DataControl_getCharacterFromBench != null
            && Seams.DataControl_getParty != null
            && Seams.DataControl_getSideBench != null
            && Seams.SkaldObjectList_getObjectList != null
            && Seams.PartyManagementStateType != null;

        private static bool OnPartyScreen()
        {
            try
            {
                object s = Pump.CurrentStateObject();
                return s != null && Seams.PartyManagementStateType.IsInstanceOfType(s);
            }
            catch { return false; }
        }

        private static int CountOf(object dc, MethodInfo listGetter)
        {
            try
            {
                object list = listGetter?.Invoke(dc, null);
                var objects = list == null ? null
                    : Seams.SkaldObjectList_getObjectList.Invoke(list, null) as System.Collections.IList;
                return objects?.Count ?? -1;
            }
            catch { return -1; }
        }

        /// <summary>The moved member's name, found by id in either live list
        /// (it sits in the destination list by the time the postfix runs).</summary>
        private static string NameOf(object dc, string npcId)
        {
            try
            {
                if (Seams.SkaldBaseObject_getId == null || Seams.SkaldBaseObject_getName == null) return null;
                foreach (var getter in new[] { Seams.DataControl_getParty, Seams.DataControl_getSideBench })
                {
                    object list = getter?.Invoke(dc, null);
                    var objects = list == null ? null
                        : Seams.SkaldObjectList_getObjectList.Invoke(list, null) as System.Collections.IList;
                    if (objects == null) continue;
                    foreach (object member in objects)
                    {
                        string id = Seams.SkaldBaseObject_getId.Invoke(member, null) as string;
                        if (id != npcId) continue;
                        string raw = Seams.SkaldBaseObject_getName.Invoke(member, null) as string;
                        return string.IsNullOrWhiteSpace(raw) ? null : TextCleaner.CleanText(raw).Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        private static void Speak(object dc, string npcId, int partyBefore, string destination)
        {
            int partyAfter = CountOf(dc, Seams.DataControl_getParty);
            if (partyAfter < 0 || partyAfter == partyBefore) return; // refusal — the popup speaks
            string name = NameOf(dc, npcId) ?? "Member";
            Scaffold.SpeechService.Say(
                $"{name} to {destination}. Main Party, {partyAfter}.", "Nav");
        }

        [HarmonyPatch]
        public static class SendToBenchHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.DataControl_sendCharacterToBench;

            [HarmonyPrefix]
            static void Prefix(object __instance, out int __state)
                => __state = OnPartyScreen() ? CountOf(__instance, Seams.DataControl_getParty) : -2;

            [HarmonyPostfix]
            static void Postfix(object __instance, string __0, int __state)
            {
                if (__state >= 0) Speak(__instance, __0, __state, "Camp Followers");
            }
        }

        [HarmonyPatch]
        public static class GetFromBenchHook
        {
            [HarmonyPrepare]
            static bool Prepare() => Ready;

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() => Seams.DataControl_getCharacterFromBench;

            [HarmonyPrefix]
            static void Prefix(object __instance, out int __state)
                => __state = OnPartyScreen() ? CountOf(__instance, Seams.DataControl_getParty) : -2;

            [HarmonyPostfix]
            static void Postfix(object __instance, string __0, int __state)
            {
                if (__state >= 0) Speak(__instance, __0, __state, "Main Party");
            }
        }
    }
}

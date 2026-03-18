using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;

namespace SkaldAccessibility.Patches
{
    /// <summary>
    /// Intercepts floating text/speech bubbles created via BarkControl.Bark constructor.
    /// These are transient positional text elements (NPC speech, combat barks, etc.)
    /// </summary>
    [HarmonyPatch]
    public static class BarkInterceptPatch
    {
        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
        {
            var barkControlType = AccessTools.TypeByName("BarkControl");
            if (barkControlType == null)
            {
                Plugin.Logger?.LogError("[BarkIntercept] BarkControl type not found");
                return null;
            }

            var barkType = barkControlType.GetNestedType("Bark", BindingFlags.NonPublic);
            if (barkType == null)
            {
                Plugin.Logger?.LogError("[BarkIntercept] Bark nested type not found");
                return null;
            }

            var constructor = AccessTools.Constructor(barkType, new[]
            {
                typeof(string),  // message
                typeof(int),     // x
                typeof(int),     // y
                typeof(Color),   // textColor
                typeof(Color),   // shadowColor
                typeof(int)      // delay
            });

            if (constructor == null)
            {
                Plugin.Logger?.LogError("[BarkIntercept] Bark constructor not found");
                return null;
            }

            Plugin.Logger?.LogInfo("[BarkIntercept] Found Bark constructor for patching");
            return constructor;
        }

        [HarmonyPostfix]
        static void Postfix(string __0, int __1, int __2)
        {
            try
            {
                if (string.IsNullOrEmpty(__0)) return;

                string cleaned = TextInterceptPatch.CleanText(__0);
                if (string.IsNullOrWhiteSpace(cleaned)) return;

                // Queue bark speech (don't interrupt current speech for floating text)
                Plugin.Speech?.SpeakQueued(cleaned, "Bark");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[BarkIntercept] {ex.Message}");
            }
        }
    }
}

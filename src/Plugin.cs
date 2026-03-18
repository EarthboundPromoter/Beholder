using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using HarmonyLib;
using System;
using System.Reflection;
using SkaldAccessibility.Speech;
using SkaldAccessibility.Patches;

namespace SkaldAccessibility
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;
        internal static ScreenReaderOutput Speech;

        private Harmony _harmony;
        private bool _skaldIOPatched;

        private void Awake()
        {
            Logger = base.Logger;

            try
            {
                // Initialize screen reader output
                Speech = new ScreenReaderOutput();
                if (Speech.IsAvailable)
                {
                    Logger.LogInfo("Screen reader detected and connected.");
                    Speech.Speak("SKALD Accessibility mod loaded.", "Init");
                }
                else
                {
                    Logger.LogWarning("No screen reader detected. Speech output will be logged only.");
                }

                // Initialize state tracking (reflection-based, before Harmony)
                StateTransitionPatch.Initialize();

                // Apply Harmony patches (excludes SkaldIOPatches — deferred to Update)
                _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());

                var patchedMethods = _harmony.GetPatchedMethods();
                int count = 0;
                foreach (var method in patchedMethods)
                {
                    Logger.LogInfo($"  Patched: {method.DeclaringType?.Name}.{method.Name}");
                    count++;
                }
                Logger.LogInfo($"Harmony patches applied: {count} methods.");
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to initialize: {e}");
            }

            Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} loaded.");
        }

        private void Update()
        {
            // Poll game state each frame
            StateTransitionPatch.PollState();

            // Apply SkaldIO patches after game data is loaded (avoids premature cctor crash)
            if (!_skaldIOPatched && GameStateTracker.CurrentMode != GameMode.Unknown)
            {
                _skaldIOPatched = true;
                SkaldIOPatches.ApplyPatches(_harmony);
            }

            // Hotkey processing
            InputHandler.ProcessInput();
        }
    }
}

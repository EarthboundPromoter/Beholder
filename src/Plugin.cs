using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Reflection;
using SkaldAccessibility.Scaffold;
using SkaldAccessibility.Patches;

namespace SkaldAccessibility
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        // GUID kept identical to the BepInEx 6 era's generated value so the
        // plugin identity (and any config) carries across the toolchain flip.
        public const string Guid = "SkaldAccessibility";
        public const string Name = "Skald Accessibility";
        public const string Version = "0.2.0";

        internal static new ManualLogSource Logger;

        private Harmony _harmony;
        private bool _skaldIOPatched;

        private void Awake()
        {
            Logger = base.Logger;

            try
            {
                // Speech tier (Scaffold, ported from Sleeptalker — WP2):
                // Tolk with screen reader preferred, SAPI fallback, log-only degrade.
                SpeechService.Init();
                UnityEngine.Application.quitting += SpeechService.Shutdown;
                SpeechService.Say($"Skald Accessibility {Version} loaded.", "Init");

                // WP8: resolve the full seam manifest first (metadata only —
                // frame-0 safe), so patch classes Prepare() against it.
                Seams.ResolveAll();

                // CP1: the combat spine's composed-surface toggles (each cue
                // utterance is its own config entry — owner ruling 2026-08-18).
                CombatSpine.BindConfig(Config);

                // Apply Harmony patches (excludes SkaldIOPatches — deferred to
                // Update). Class-by-class with isolation: Harmony's own
                // PatchAll aborts every remaining class when one throws
                // (verified against the shipped 0Harmony.dll) — one broken
                // seam must cost one stream, not the mod.
                _harmony = new Harmony(Guid);
                foreach (var type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
                {
                    try { _harmony.CreateClassProcessor(type).Patch(); }
                    catch (Exception e) { Seams.NotePatchFailure(type.FullName, e); }
                }

                var patchedMethods = _harmony.GetPatchedMethods();
                int count = 0;
                foreach (var method in patchedMethods)
                {
                    Logger.LogInfo($"  Patched: {method.DeclaringType?.Name}.{method.Name}");
                    count++;
                }
                Logger.LogInfo($"Harmony patches applied: {count} methods.");

                // The audit receipt: every missing row logged, one spoken line
                // if anything is gone ("N game hooks missing after an update").
                Seams.Report();
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to initialize: {e}");
            }

            Logger.LogInfo($"Plugin {Name} v{Version} loaded.");
        }

        private void Update()
        {
            // Apply SkaldIO patches after game data is loaded (avoids premature cctor
            // crash). Keys off the first state classification, which now arrives via
            // the setState clock at the Pump's drain.
            if (!_skaldIOPatched && GameStateTracker.CurrentMode != GameMode.Unknown)
            {
                _skaldIOPatched = true;
                SkaldIOPatches.ApplyPatches(_harmony);
            }

            // Hotkey processing
            InputHandler.ProcessInput();
        }

        private void LateUpdate()
        {
            // The timing spine: note-only hooks feed pending state; all reads,
            // composition, and the speech queue pump happen here, once per frame,
            // after the game's own update has settled.
            Pump.Drain();
        }
    }
}

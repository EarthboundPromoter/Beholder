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
        public const string Name = "Beholder";
        public const string Version = "0.5.8";

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
                SpeechService.Say($"{Name} {Version} loaded.", "Init");

                // WP8: resolve the full seam manifest first (metadata only —
                // frame-0 safe), so patch classes Prepare() against it.
                Seams.ResolveAll();

                // CP1: the combat spine's composed-surface toggles (each cue
                // utterance is its own config entry — owner ruling 2026-08-18).
                CombatSpine.BindConfig(Config);
                // CP3: the combat cursor's toggles (light tail, placement
                // qualifier position, census).
                CombatCursor.BindConfig(Config);
                // TP1: the panel policy — AutoReadBody plus the strip's
                // per-fact toggles (weather/phase on, clock/position off).
                PanelPolicy.BindConfig(Config);
                // POI list: close-on-use opt-in (default: persistent list,
                // owner ruling 2026-08-19).
                OverlandCursor.BindConfig(Config);
                // Diagnosability scaffold: per-channel debug gating (owner
                // directive 2026-08-19).
                Scaffold.Log.BindConfig(Config);
                // Table-UI foundation: the scrollbar arrow reclaim (gate
                // item 1) + the verification-gate instrumentation (items 2-4).
                Patches.ScrollbarReclaimPatch.BindConfig(Config);
                GateReceipts.BindConfig(Config);
                // Table engine (gate A): the R15 arrows-as-rows / WASD-as-
                // sections grammar on registered UI screens.
                TableCursor.BindConfig(Config);
                // Tile-art transcode: overland art nouns + tile coordinates
                // (owner go 2026-08-29; combat deliberately excluded).
                TileArtTable.BindConfig(Config);
                // F1 contextual key table (owner design 2026-08-29).
                KeyTable.BindConfig(Config);
                // Silent auto-rebind of the two standing game rebinds
                // (owner ruling 2026-08-29).
                RebindGuard.BindConfig(Config);
                // Overland reachability verdict (owner go 2026-08-29,
                // wayfinding tier 1).
                TileReach.BindConfig(Config);

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

                // Build identity (L4): two logs of the same version string
                // proved to be different builds in the Opus review — the DLL
                // write time disambiguates without any build machinery.
                try
                {
                    var loc = Assembly.GetExecutingAssembly().Location;
                    Logger.LogInfo($"[Build] v{Version} dll={System.IO.File.GetLastWriteTime(loc):yyyy-MM-dd'T'HH:mm:ss} "
                        + $"session={DateTime.Now:yyyy-MM-dd'T'HH:mm:ss}");
                }
                catch { }
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

            // Hotkey processing, then the press latch (audit 2026-09-02):
            // after the mod's own layers consumed what they own, this frame's
            // key-downs arm the feed's per-key flags for the game's next
            // FixedUpdate tick — the Update-phase latch the game gives its own
            // keys in SkaldIO.update. `finally`: a throwing mod layer must
            // cost mod features, never the game's own keyboard.
            // The whole Update-phase body is stopwatched (FrameBudget, the
            // frame-budget receipt 2026-09-03): this is where the overland
            // passive sweep runs.
            FrameBudget.Begin();
            try
            {
                try { InputHandler.ProcessInput(); }
                finally { SkaldIOPatches.ArmPressLatch(); }

                // Verification-gate instrumentation (table-UI foundation; log-only)
                GateReceipts.Tick();
            }
            finally { FrameBudget.End(); }
        }

        private void LateUpdate()
        {
            // The timing spine: note-only hooks feed pending state; all reads,
            // composition, and the speech queue pump happen here, once per frame,
            // after the game's own update has settled. Stopwatched as the
            // frame's second mod segment (the speech pump's synchronous Tolk
            // call lands here); then the previous frame is judged.
            FrameBudget.Begin();
            try { Pump.Drain(); }
            finally { FrameBudget.End(); }
            FrameBudget.EndFrame();
            Scaffold.Log.ClockTick();   // wall-clock stamp ~every 10s (L3)
        }

        /// <summary>Session-end receipt (L9): the line's PRESENCE in a
        /// watcher-captured log distinguishes a clean quit from a crash.</summary>
        private void OnApplicationQuit()
        {
            Scaffold.Log.ClockNow();
            Logger.LogInfo("[Session] Clean exit (OnApplicationQuit).");
        }
    }
}

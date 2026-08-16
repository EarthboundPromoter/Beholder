using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;

namespace SkaldBridge
{
    /// <summary>
    /// Dev-only observation bridge (lineage idiom: OagBridge / Sleeptalker bridge).
    /// Minimal single-threaded HTTP server on 127.0.0.1:8332, GET only.
    /// TcpListener (not HttpListener) to avoid URL-ACL. Bind-with-retry because a
    /// Steam DRM relaunch can leave the dying instance holding the port.
    ///
    /// Observation-only except /quit (dev-only drive endpoint, owner-sanctioned
    /// class). Local-only, gitignored, never ships.
    ///
    /// Endpoints:
    ///   /ping   — bridge alive, current frame
    ///   /status — loaded plugins, mod version, current game state, uptime
    ///   /state  — current StateBase type + its guiControl type
    ///   /log?n=NN — tail of BepInEx LogOutput.log (speech receipts included)
    ///   /quit   — Application.Quit on the main thread
    /// </summary>
    [BepInPlugin("SkaldBridge", "Skald Bridge", BridgeVersion)]
    public class BridgePlugin : BaseUnityPlugin
    {
        public const string BridgeVersion = "0.1.0";
        private const int Port = 8332;

        private static readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();
        private static volatile bool _stop;
        private Thread _serverThread;

        // Cached reflection for /state
        private static Type _mainControlType;
        private static FieldInfo _gameControlField;
        private static FieldInfo _currentStateField;
        private static FieldInfo _stateGuiControlField;
        private static UnityEngine.Object _mainControlInstance;

        private void Awake()
        {
            _serverThread = new Thread(ServerLoop) { IsBackground = true, Name = "SkaldBridge" };
            _serverThread.Start();
            Application.quitting += () => _stop = true;
            Logger.LogInfo($"[Bridge] SkaldBridge {BridgeVersion} starting on 127.0.0.1:{Port}");
        }

        private void Update()
        {
            while (MainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Logger.LogWarning($"[Bridge] main-thread action failed: {ex.Message}"); }
            }
        }

        private void ServerLoop()
        {
            TcpListener listener = null;
            // Bind with retry (~60s) — Steam relaunch may briefly hold the port.
            for (int attempt = 0; attempt < 60 && !_stop; attempt++)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, Port);
                    listener.Start();
                    break;
                }
                catch (SocketException)
                {
                    listener = null;
                    Thread.Sleep(1000);
                }
            }
            if (listener == null) { Logger.LogError("[Bridge] could not bind port; bridge disabled"); return; }
            Logger.LogInfo($"[Bridge] listening on 127.0.0.1:{Port}");

            while (!_stop)
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        stream.ReadTimeout = 2000;
                        string requestLine = ReadRequestLine(stream);
                        string body = Handle(requestLine);
                        byte[] payload = Encoding.UTF8.GetBytes(body);
                        string header = "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\n"
                                      + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                        byte[] head = Encoding.ASCII.GetBytes(header);
                        stream.Write(head, 0, head.Length);
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();
                        // Graceful close: half-close our side and drain the peer's
                        // FIN before disposing, so the OS cannot discard buffered
                        // response bytes with an RST (the intermittent-empty-response
                        // bug from the first boot test).
                        try
                        {
                            client.Client.Shutdown(SocketShutdown.Send);
                            var drain = new byte[256];
                            while (stream.Read(drain, 0, drain.Length) > 0) { }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    if (!_stop) Logger.LogDebug($"[Bridge] request error: {ex.Message}");
                }
            }
            try { listener.Stop(); } catch { }
        }

        private static string ReadRequestLine(NetworkStream stream)
        {
            var sb = new StringBuilder();
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                if (b == '\n') break;
                if (b != '\r') sb.Append((char)b);
                if (sb.Length > 2048) break;
            }
            return sb.ToString();
        }

        private string Handle(string requestLine)
        {
            // "GET /path?query HTTP/1.1"
            string path = "";
            string query = "";
            var parts = requestLine.Split(' ');
            if (parts.Length >= 2)
            {
                var pq = parts[1].Split(new[] { '?' }, 2);
                path = pq[0];
                query = pq.Length > 1 ? pq[1] : "";
            }

            switch (path)
            {
                case "/ping":
                    return $"{{\"ok\":true,\"bridge\":\"{BridgeVersion}\",\"frame\":{Time.frameCount}}}";
                case "/status":
                    return OnMainThread(StatusJson);
                case "/state":
                    return OnMainThread(StateJson);
                case "/log":
                    return LogTailJson(query);
                case "/speech":
                    return OnMainThread(SpeechJson);
                case "/press":
                    return OnMainThread(() => PressJson(query));
                case "/screenshot":
                    return OnMainThread(ScreenshotJson);
                case "/quit":
                    MainThreadQueue.Enqueue(() => Application.Quit());
                    return "{\"ok\":true,\"action\":\"quit queued\"}";
                default:
                    return "{\"ok\":false,\"error\":\"unknown endpoint\",\"endpoints\":[\"/ping\",\"/status\",\"/state\",\"/log?n=NN\",\"/quit\"]}";
            }
        }

        /// <summary>Run a producer on the main thread, wait up to 2s for its result.</summary>
        private static string OnMainThread(Func<string> producer)
        {
            string result = null;
            using (var done = new ManualResetEventSlim(false))
            {
                MainThreadQueue.Enqueue(() =>
                {
                    try { result = producer(); }
                    catch (Exception ex) { result = $"{{\"ok\":false,\"error\":\"{Escape(ex.Message)}\"}}"; }
                    finally { done.Set(); }
                });
                if (!done.Wait(2000))
                    return "{\"ok\":false,\"error\":\"main thread timeout\"}";
            }
            return result ?? "{\"ok\":false,\"error\":\"no result\"}";
        }

        /// <summary>Dev-only drive (owner-sanctioned 2026-08-16): arm a one-shot
        /// synthetic press consumed by the mod's own input postfixes next frame —
        /// full mod-path parity. /press?key=up|down|left|right|confirm|cancel</summary>
        private static string PressJson(string query)
        {
            string key = null;
            foreach (var kv in query.Split('&'))
            {
                var p = kv.Split(new[] { '=' }, 2);
                if (p.Length == 2 && p[0] == "key") key = p[1].ToLowerInvariant();
            }
            var patches = AccessTools.TypeByName("SkaldAccessibility.Patches.SkaldIOPatches");
            if (patches == null) return "{\"ok\":false,\"error\":\"SkaldIOPatches not found\"}";
            string fieldName;
            switch (key)
            {
                case "up": fieldName = "InjectUpFrame"; break;
                case "down": fieldName = "InjectDownFrame"; break;
                case "left": fieldName = "InjectLeftFrame"; break;
                case "right": fieldName = "InjectRightFrame"; break;
                case "confirm": fieldName = "InjectConfirmFrame"; break;
                case "cancel": fieldName = "InjectCancelFrame"; break;
                default: return "{\"ok\":false,\"error\":\"key must be up|down|left|right|confirm|cancel\"}";
            }
            var field = AccessTools.Field(patches, fieldName);
            if (field == null) return "{\"ok\":false,\"error\":\"inject field not found (old mod build?)\"}";
            field.SetValue(null, Time.frameCount + 1);
            return $"{{\"ok\":true,\"pressed\":\"{key}\",\"armedForFrame\":{Time.frameCount + 1}}}";
        }

        private static string ScreenshotJson()
        {
            string dir = Path.Combine(Paths.GameRootPath, "BepInEx", "bridge_shots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"shot_{Time.frameCount}.png");
            ScreenCapture.CaptureScreenshot(path);
            return $"{{\"ok\":true,\"path\":\"{Escape(path)}\",\"note\":\"written async, poll the file\"}}";
        }

        /// <summary>Recent utterances + queue depth, reflected out of the mod's
        /// SpeechService (dev-only coupling; tolerates the mod being absent).</summary>
        private static string SpeechJson()
        {
            try
            {
                var svc = AccessTools.TypeByName("SkaldAccessibility.Scaffold.SpeechService");
                if (svc == null) return "{\"ok\":false,\"error\":\"SpeechService not found\"}";
                var recent = AccessTools.Method(svc, "RecentHistory")
                    .Invoke(null, new object[] { 20 }) as System.Collections.Generic.List<string>;
                int depth = (int)AccessTools.Property(svc, "QueueDepth").GetValue(null, null);
                var lines = recent == null ? "" : string.Join(",", recent.Select(l => $"\"{Escape(l)}\""));
                return $"{{\"ok\":true,\"queueDepth\":{depth},\"recent\":[{lines}]}}";
            }
            catch (Exception ex)
            {
                return $"{{\"ok\":false,\"error\":\"{Escape(ex.Message)}\"}}";
            }
        }

        private static string StatusJson()
        {
            var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos;
            var names = string.Join(",", plugins.Values.Select(p =>
                $"\"{Escape(p.Metadata.Name)} {p.Metadata.Version}\""));
            string state = CurrentStateName() ?? "unknown";
            return $"{{\"ok\":true,\"plugins\":[{names}],\"state\":\"{Escape(state)}\","
                 + $"\"frame\":{Time.frameCount},\"uptimeSeconds\":{(int)Time.realtimeSinceStartup},"
                 + $"\"unity\":\"{Escape(Application.unityVersion)}\",\"gameVersion\":\"{Escape(Application.version)}\"}}";
        }

        private static string StateJson()
        {
            object state = CurrentStateObject();
            if (state == null) return "{\"ok\":true,\"state\":null}";
            string gui = "null";
            try
            {
                if (_stateGuiControlField == null)
                    _stateGuiControlField = AccessTools.Field(AccessTools.TypeByName("StateBase"), "guiControl");
                var guiObj = _stateGuiControlField?.GetValue(state);
                if (guiObj != null) gui = $"\"{Escape(guiObj.GetType().Name)}\"";
            }
            catch { }
            return $"{{\"ok\":true,\"state\":\"{Escape(state.GetType().Name)}\",\"guiControl\":{gui}}}";
        }

        private static string CurrentStateName()
        {
            var s = CurrentStateObject();
            return s?.GetType().Name;
        }

        private static object CurrentStateObject()
        {
            try
            {
                if (_mainControlType == null)
                {
                    _mainControlType = AccessTools.TypeByName("MainControl");
                    if (_mainControlType == null) return null;
                    _gameControlField = AccessTools.Field(_mainControlType, "gameControl");
                }
                if (_mainControlInstance == null)
                    _mainControlInstance = UnityEngine.Object.FindObjectOfType(_mainControlType);
                if (_mainControlInstance == null || _gameControlField == null) return null;

                object stateControl = _gameControlField.GetValue(_mainControlInstance);
                if (stateControl == null) return null;
                if (_currentStateField == null)
                    _currentStateField = AccessTools.Field(stateControl.GetType(), "currentState");
                return _currentStateField?.GetValue(stateControl);
            }
            catch { return null; }
        }

        private static string LogTailJson(string query)
        {
            int n = 50;
            foreach (var kv in query.Split('&'))
            {
                var p = kv.Split(new[] { '=' }, 2);
                if (p.Length == 2 && p[0] == "n" && int.TryParse(p[1], out int parsed))
                    n = Math.Max(1, Math.Min(500, parsed));
            }
            try
            {
                string logPath = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
                string[] lines;
                // Share-friendly read — BepInEx holds the file open.
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs))
                    lines = reader.ReadToEnd().Split('\n');
                var tail = lines.Skip(Math.Max(0, lines.Length - n))
                                .Select(l => $"\"{Escape(l.TrimEnd('\r'))}\"");
                return $"{{\"ok\":true,\"lines\":[{string.Join(",", tail)}]}}";
            }
            catch (Exception ex)
            {
                return $"{{\"ok\":false,\"error\":\"{Escape(ex.Message)}\"}}";
            }
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}

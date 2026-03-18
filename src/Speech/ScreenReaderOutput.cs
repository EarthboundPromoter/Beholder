using System;
using System.Runtime.InteropServices;

namespace SkaldAccessibility.Speech
{
    /// <summary>
    /// Direct P/Invoke wrapper for NVDA's controller client DLL.
    /// Non-blocking: NVDA handles speech asynchronously.
    /// Falls back to log-only if NVDA is not running.
    /// </summary>
    public class ScreenReaderOutput
    {
        [DllImport("nvdaControllerClient64.dll", CharSet = CharSet.Unicode)]
        private static extern int nvdaController_speakText(string text);

        [DllImport("nvdaControllerClient64.dll", CharSet = CharSet.Unicode)]
        private static extern int nvdaController_cancelSpeech();

        [DllImport("nvdaControllerClient64.dll")]
        private static extern int nvdaController_testIfRunning();

        [DllImport("nvdaControllerClient64.dll", CharSet = CharSet.Unicode)]
        private static extern int nvdaController_brailleMessage(string text);

        private bool _available;
        private string _lastSpoken;
        private readonly object _lock = new object();

        /// <summary>
        /// Whether a screen reader was detected at initialization.
        /// </summary>
        public bool IsAvailable => _available;

        public ScreenReaderOutput()
        {
            try
            {
                _available = nvdaController_testIfRunning() == 0;
            }
            catch (DllNotFoundException)
            {
                _available = false;
                Plugin.Logger?.LogWarning("nvdaControllerClient64.dll not found. NVDA integration disabled.");
            }
            catch (Exception ex)
            {
                _available = false;
                Plugin.Logger?.LogWarning($"NVDA check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Speak text through the screen reader. Non-blocking. Interrupts any current speech.
        /// source identifies the patch/system that triggered the speech (for log attribution).
        /// </summary>
        public void Speak(string text, string source = null)
        {
            if (string.IsNullOrEmpty(text)) return;

            lock (_lock)
            {
                _lastSpoken = text;
            }

            SpeechHistory.Instance.Add(text);
            Plugin.Logger?.LogInfo(source != null ? $"[Speech:{source}] {text}" : $"[Speech] {text}");

            if (!_available) return;

            try
            {
                nvdaController_cancelSpeech();
                nvdaController_speakText(text);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"Speech failed: {ex.Message}");
                _available = false;
            }
        }

        /// <summary>
        /// Speak text without interrupting current speech (queued).
        /// source identifies the patch/system that triggered the speech (for log attribution).
        /// </summary>
        public void SpeakQueued(string text, string source = null)
        {
            if (string.IsNullOrEmpty(text)) return;

            lock (_lock)
            {
                _lastSpoken = text;
            }

            SpeechHistory.Instance.Add(text);
            Plugin.Logger?.LogInfo(source != null ? $"[Speech+:{source}] {text}" : $"[Speech+] {text}");

            if (!_available) return;

            try
            {
                nvdaController_speakText(text);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"Speech failed: {ex.Message}");
                _available = false;
            }
        }

        /// <summary>
        /// Cancel any current speech.
        /// </summary>
        public void Cancel()
        {
            if (!_available) return;

            try
            {
                nvdaController_cancelSpeech();
            }
            catch { }
        }

        /// <summary>
        /// Send text to braille display.
        /// </summary>
        public void Braille(string text)
        {
            if (string.IsNullOrEmpty(text) || !_available) return;

            try
            {
                nvdaController_brailleMessage(text);
            }
            catch { }
        }

        /// <summary>
        /// Get the last spoken text (for speech history).
        /// </summary>
        public string LastSpoken
        {
            get { lock (_lock) { return _lastSpoken; } }
        }

        /// <summary>
        /// Re-check if screen reader is available (e.g., after NVDA restart).
        /// </summary>
        public void Recheck()
        {
            try
            {
                _available = nvdaController_testIfRunning() == 0;
            }
            catch
            {
                _available = false;
            }
        }
    }
}

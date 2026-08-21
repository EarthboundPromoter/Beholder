using BepInEx.Configuration;
using UnityEngine;

namespace SkaldAccessibility
{
    /// <summary>
    /// Table-UI verification-gate instrumentation (docs\table-ui-design.md §10,
    /// items 2–4). One instrumented ride produces the receipts in the session
    /// log; everything here is read-only observation.
    ///
    ///  - Gate 2 (stick-axis runtime check): does Unity's legacy Horizontal/
    ///    Vertical axis — the left-stick source, SkaldIO.cs:146/197 — carry
    ///    keyboard keys with no controller attached? Statically unresolvable;
    ///    we sample the axes every frame and log whenever they fire, with the
    ///    arrow/WASD key state alongside. A ride full of arrow and WASD input
    ///    with no [Gate] axis lines = the axes are clean (the armed line at
    ///    boot is the positive control that sampling ran).
    ///
    ///  - Gate 3 (hover-park receipt): every virtual-mouse park already flows
    ///    through the one setter (MouseGuardPatch's postfix); parks are
    ///    counted and sampled here, and hover products correlate through the
    ///    existing speech/panel logs at the logged frames.
    ///
    ///  - Gate 4 (click-plumbing receipt): Z's press/release edges logged
    ///    against the game's own mouse-click merge results
    ///    (getMousePressed(0)/getMouseUp(0) — the LT→click path), plus the
    ///    rapid-pair timing that measures the double-click cooldown window.
    /// </summary>
    internal static class GateReceipts
    {
        private static ConfigEntry<bool> _cfg;
        private static bool _armed;
        private static float _lastAxisLog;
        private static int _parkCount;
        private static float _lastZDownTime = -1000f;

        internal static void BindConfig(ConfigFile config)
        {
            _cfg = config.Bind("Gate", "Instrumentation", true,
                "Table-UI verification-gate instrumentation (gate items 2-4): axis sampling, park counting, click-plumbing receipts. Log-only.");
        }

        private static bool Enabled => _cfg != null && _cfg.Value;

        /// <summary>Called from Plugin.Update, every frame.</summary>
        internal static void Tick()
        {
            if (!Enabled) return;

            if (!_armed)
            {
                _armed = true;
                string sticks;
                try
                {
                    var names = Input.GetJoystickNames();
                    sticks = names.Length == 0 ? "none" : string.Join("; ", names);
                }
                catch { sticks = "unreadable"; }
                Scaffold.Log.Debug("Gate", $"instrumentation armed — axis sampling live; joysticks: {sticks} (receipts 2-4)");
            }

            SampleAxes();
            SampleClickPlumbing();
        }

        // ---- Gate 2: the axis check ----

        private static void SampleAxes()
        {
            float h, v;
            try
            {
                h = Input.GetAxis("Horizontal");
                v = Input.GetAxis("Vertical");
            }
            catch { return; }
            if (Mathf.Abs(h) < 0.15f && Mathf.Abs(v) < 0.15f) return;
            if (Time.unscaledTime - _lastAxisLog < 1f) return;
            _lastAxisLog = Time.unscaledTime;

            string keys = "";
            if (Input.GetKey(KeyCode.UpArrow)) keys += "Up ";
            if (Input.GetKey(KeyCode.DownArrow)) keys += "Down ";
            if (Input.GetKey(KeyCode.LeftArrow)) keys += "Left ";
            if (Input.GetKey(KeyCode.RightArrow)) keys += "Right ";
            if (Input.GetKey(KeyCode.W)) keys += "W ";
            if (Input.GetKey(KeyCode.A)) keys += "A ";
            if (Input.GetKey(KeyCode.S)) keys += "S ";
            if (Input.GetKey(KeyCode.D)) keys += "D ";
            Scaffold.Log.Debug("Gate",
                $"AXIS FIRED H={h:0.00} V={v:0.00} keysHeld=[{keys.TrimEnd()}] f{Time.frameCount} (receipt 2)");
        }

        // ---- Gate 3: park observation (fed by MouseGuardPatch) ----

        /// <summary>Every non-reassert virtual-mouse park flows through here.
        /// First park and every 50th are logged; hover products correlate via
        /// the existing speech/panel logs at these frames.</summary>
        internal static void NotePark(int x, int y)
        {
            if (!Enabled) return;
            _parkCount++;
            if (_parkCount == 1 || _parkCount % 50 == 0)
                Scaffold.Log.Debug("Gate", $"vmouse park #{_parkCount} at ({x},{y}) f{Time.frameCount} (receipt 3)");
        }

        // ---- Gate 4: click plumbing ----

        private static void SampleClickPlumbing()
        {
            if (Patches.ControllerFeedPatch.TextEntryActive()) return;
            if (Input.GetKeyDown(KeyCode.Z))
            {
                bool pressed = false;
                try
                {
                    if (Seams.SkaldIO_getMousePressed != null)
                        pressed = (bool)Seams.SkaldIO_getMousePressed.Invoke(null, new object[] { 0 });
                }
                catch { }
                float sincePrev = Time.unscaledTime - _lastZDownTime;
                string pair = sincePrev < 0.5f ? $", rapid pair +{sincePrev * 1000f:0}ms" : "";
                _lastZDownTime = Time.unscaledTime;
                Scaffold.Log.Debug("Gate", $"Z down f{Time.frameCount} mousePressed={pressed}{pair} (receipt 4)");
            }
            if (Input.GetKeyUp(KeyCode.Z))
            {
                bool up = false;
                try
                {
                    if (Seams.SkaldIO_getMouseUp != null)
                        up = (bool)Seams.SkaldIO_getMouseUp.Invoke(null, new object[] { 0 });
                }
                catch { }
                Scaffold.Log.Debug("Gate", $"Z up f{Time.frameCount} mouseUp={up} (receipt 4)");
            }
        }
    }
}

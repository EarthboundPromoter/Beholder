using System.Collections.Generic;

namespace SkaldAccessibility.Speech
{
    /// <summary>
    /// Circular buffer of recent speech announcements.
    /// Navigable via hotkeys for re-reading.
    /// </summary>
    public class SpeechHistory
    {
        public static readonly SpeechHistory Instance = new SpeechHistory();

        private readonly List<string> _history = new List<string>();
        private readonly int _maxSize;
        private int _cursor = -1;

        public SpeechHistory(int maxSize = 50)
        {
            _maxSize = maxSize;
        }

        public void Add(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            _history.Add(text);
            if (_history.Count > _maxSize)
                _history.RemoveAt(0);

            // Reset cursor to end
            _cursor = _history.Count;
        }

        /// <summary>
        /// Navigate backward in history. Returns the entry or null if at start.
        /// </summary>
        public string Previous()
        {
            if (_history.Count == 0) return null;
            _cursor--;
            if (_cursor < 0) _cursor = 0;
            return _history[_cursor];
        }

        /// <summary>
        /// Navigate forward in history. Returns the entry or null if at end.
        /// </summary>
        public string Next()
        {
            if (_history.Count == 0) return null;
            _cursor++;
            if (_cursor >= _history.Count) _cursor = _history.Count - 1;
            return _history[_cursor];
        }

        public int Count => _history.Count;
    }
}

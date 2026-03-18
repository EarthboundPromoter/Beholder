using System.Runtime.CompilerServices;

namespace SkaldAccessibility
{
    /// <summary>
    /// Authoritative cursor state for keyboard navigation.
    ///
    /// Owns a selection index per control instance. Arrow keys move the index
    /// directly — no hover search. The virtual mouse is positioned to match
    /// by the navigation patches. The game's hover system becomes a side effect.
    ///
    /// Keyed by control instance via ConditionalWeakTable so entries are
    /// garbage-collected when the game destroys the control.
    /// </summary>
    public static class NavigationCursor
    {
        public class CursorState
        {
            public int Index = -1;
            public string LastSpoken;
        }

        private static readonly ConditionalWeakTable<object, CursorState> _cursors =
            new ConditionalWeakTable<object, CursorState>();

        /// <summary>
        /// Move cursor by delta, clamped to [0, count-1]. Returns new index.
        /// Does nothing if count is 0 or control is null.
        /// </summary>
        public static int MoveCursor(object control, int delta, int count)
        {
            if (control == null || count <= 0) return -1;
            var state = _cursors.GetOrCreateValue(control);
            if (state.Index < 0) state.Index = 0;
            state.Index += delta;
            if (state.Index < 0) state.Index = 0;
            if (state.Index >= count) state.Index = count - 1;
            return state.Index;
        }

        /// <summary>
        /// Set cursor to a specific index.
        /// </summary>
        public static void SetIndex(object control, int index)
        {
            if (control == null) return;
            var state = _cursors.GetOrCreateValue(control);
            state.Index = index;
        }

        /// <summary>
        /// Get current index. Returns -1 if untracked.
        /// </summary>
        public static int GetIndex(object control)
        {
            if (control == null) return -1;
            return _cursors.GetOrCreateValue(control).Index;
        }

        /// <summary>
        /// Get the full cursor state (for speech dedup).
        /// </summary>
        public static CursorState GetState(object control)
        {
            if (control == null) return null;
            return _cursors.GetOrCreateValue(control);
        }
    }
}

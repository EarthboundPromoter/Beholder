using System;
using System.Runtime.InteropServices;

namespace SkaldAccessibility.Scaffold
{
    /// <summary>P/Invoke bindings for Tolk.dll (expected beside the game executable).
    /// Standard Tolk contract: wide strings, I1 bools, cdecl.
    /// Ported verbatim from Sleeptalker Scaffold (WP2, 2026-08-16).</summary>
    internal static class Tolk
    {
        private const string Dll = "Tolk.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Tolk_Load();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Tolk_IsLoaded();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Tolk_Unload();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Tolk_TrySAPI([MarshalAs(UnmanagedType.I1)] bool trySAPI);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Tolk_PreferSAPI([MarshalAs(UnmanagedType.I1)] bool preferSAPI);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr Tolk_DetectScreenReader();

        [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Tolk_Output([MarshalAs(UnmanagedType.LPWStr)] string str,
            [MarshalAs(UnmanagedType.I1)] bool interrupt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Tolk_IsSpeaking();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Tolk_Silence();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Tolk_HasSpeech();

        public static string DetectScreenReader()
        {
            var ptr = Tolk_DetectScreenReader();
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr);
        }
    }
}

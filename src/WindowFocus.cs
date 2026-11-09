using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LanaVR
{
    /// <summary>
    /// Brings the game's window to the front when a VR session starts. A game started from a headset's menu can
    /// open behind another window, and a real gamepad then does nothing until the window is clicked, which is
    /// awkward with a headset on. Unity's Application.isFocused still reads true in that state, as the window has
    /// never been told it lost focus, so this asks Windows which window is really in front.
    /// </summary>
    internal static class WindowFocus
    {
        // Only for a short time, so it never fights a player who switches away on purpose later on.
        const float GiveUpAfter = 30f, HeldForLongEnough = 5f;

        static float _start = -1, _heldSince = -1;
        static bool _finished, _loggedDone;

        [DllImport("user32")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32")] static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32")] static extern bool AttachThreadInput(uint attach, uint to, bool enable);
        [DllImport("user32")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [DllImport("kernel32")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32")] static extern uint GetCurrentProcessId();

        const byte AltKey = 0x12;
        const uint KeyUp = 2;

        public static bool IsForeground
        {
            get
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
                return process == GetCurrentProcessId();
            }
        }

        public static string Status => $"foreground={IsForeground} claimFinished={_finished}";

        /// <summary>Called twice a second during a VR session.</summary>
        public static void Poll()
        {
            if (_finished) return;
            float now = Time.unscaledTime;
            if (_start < 0) _start = now;
            if (IsForeground)
            {
                if (_heldSince < 0) _heldSince = now;
                if (now - _heldSince >= HeldForLongEnough) Finish("the game's window is in front");
                return;
            }
            _heldSince = -1;
            if (now - _start >= GiveUpAfter) { Finish("gave up after 30 s, click the game's window if the gamepad does nothing"); return; }
            Claim();
        }

        static void Finish(string why)
        {
            _finished = true;
            if (_loggedDone) return;
            _loggedDone = true;
            Plugin.L.LogInfo("Window focus: " + why);
        }

        static void Claim()
        {
            IntPtr window;
            using (var process = Process.GetCurrentProcess()) window = process.MainWindowHandle;
            if (window == IntPtr.Zero) return;
            // Windows only lets the program in front hand over the foreground. Sharing its input queue for a moment,
            // and a tap of Alt, count as that permission.
            uint front = GetWindowThreadProcessId(GetForegroundWindow(), out _), me = GetCurrentThreadId();
            keybd_event(AltKey, 0, 0, UIntPtr.Zero);
            keybd_event(AltKey, 0, KeyUp, UIntPtr.Zero);
            bool attached = front != 0 && front != me && AttachThreadInput(me, front, true);
            BringWindowToTop(window);
            SetForegroundWindow(window);
            if (attached) AttachThreadInput(me, front, false);
        }
    }
}

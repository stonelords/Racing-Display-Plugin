using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RaceDisplay
{
    /// <summary>
    /// Mouse wheel turns, read with a Windows low-level mouse hook (the plugin has no other way to see the wheel
    /// while the game has the mouse). The hook lives on its own thread with its own message loop, only while
    /// something is watching, and does nothing but add up the wheel's movement.
    /// </summary>
    internal static class MouseWheel
    {
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInfo { public int X, Y; public uint MouseData, Flags, Time; public IntPtr Extra; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr module, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);

        private const int WH_MOUSE_LL = 14, WM_MOUSEWHEEL = 0x020A, WM_QUIT = 0x0012;

        private static readonly object gate = new object();
        private static Thread thread;
        private static uint threadId;
        private static IntPtr hook;
        private static HookProc proc;   // kept so the delegate isn't collected while Windows still calls it
        private static int turned;
        private static bool wanted;

        /// <summary>Starts or stops watching the wheel.</summary>
        public static void Watch(bool on)
        {
            lock (gate)
            {
                wanted = on;
                if (on)
                {
                    if (thread != null) return;
                    Interlocked.Exchange(ref turned, 0);
                    thread = new Thread(Run) { IsBackground = true, Name = "RaceDisplay mouse wheel" };
                    thread.Start();
                }
                else if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
        }

        /// <summary>Wheel movement since the last call: 120 a notch, + = away from you.</summary>
        public static int Take() => Interlocked.Exchange(ref turned, 0);

        private static void Run()
        {
            try
            {
                proc = OnMouse;
                lock (gate)
                {
                    threadId = GetCurrentThreadId();
                    if (wanted) hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
                }
                if (hook == IntPtr.Zero)
                {
                    if (wanted) PluginLog.Info("Mouse wheel: couldn't hook it (error " + Marshal.GetLastWin32Error() + "); Ctrl+wheel won't work");
                }
                else
                {
                    PluginLog.Info("Mouse wheel: watching");
                    while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
                }
            }
            catch (Exception e) { PluginLog.Error("Mouse wheel watcher failed", e); }
            finally
            {
                bool again;
                lock (gate)
                {
                    if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
                    threadId = 0;
                    thread = null;
                    again = wanted;   // asked for again while this one was closing
                }
                if (again) Watch(true);
            }
        }

        private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && (int)wParam == WM_MOUSEWHEEL)
                {
                    var info = Marshal.PtrToStructure<MouseInfo>(lParam);
                    Interlocked.Add(ref turned, (short)(info.MouseData >> 16));
                }
            }
            catch { /* never hold the mouse up */ }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }
    }
}

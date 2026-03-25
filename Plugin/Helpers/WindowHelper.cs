using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MTGAEnhancementSuite.Helpers
{
    /// <summary>
    /// Shared helper for bringing the MTGA window to the foreground.
    /// Used by TcpIpcServer (link clicks) and ChallengeJoinPatch (player joins lobby).
    /// </summary>
    internal static class WindowHelper
    {
#if !UNITY_STANDALONE_OSX
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;
#endif

        /// <summary>
        /// Brings the MTGA window to the foreground using the AttachThreadInput trick
        /// to bypass Windows' restriction that only the foreground process can steal focus.
        /// </summary>
        public static void BringToFront()
        {
#if !UNITY_STANDALONE_OSX
            try
            {
                var proc = Process.GetCurrentProcess();
                IntPtr hWnd = proc.MainWindowHandle;

                if (hWnd == IntPtr.Zero)
                {
                    Plugin.Log.LogWarning("WindowHelper: Could not find MTGA main window handle");
                    return;
                }

                if (IsIconic(hWnd))
                    ShowWindow(hWnd, SW_RESTORE);
                else
                    ShowWindow(hWnd, SW_SHOW);

                IntPtr foregroundHwnd = GetForegroundWindow();
                uint foregroundThread = GetWindowThreadProcessId(foregroundHwnd, out _);
                uint currentThread = GetCurrentThreadId();

                if (foregroundThread != currentThread)
                {
                    AttachThreadInput(currentThread, foregroundThread, true);
                    BringWindowToTop(hWnd);
                    SetForegroundWindow(hWnd);
                    AttachThreadInput(currentThread, foregroundThread, false);
                }
                else
                {
                    BringWindowToTop(hWnd);
                    SetForegroundWindow(hWnd);
                }

                Plugin.Log.LogInfo("WindowHelper: Brought MTGA window to foreground");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WindowHelper: Failed to bring window to front: {ex.Message}");
            }
#endif
        }
    }
}

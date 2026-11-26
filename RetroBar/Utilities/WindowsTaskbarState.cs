using ManagedShell.Common.Logging;
using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Controls the Windows taskbar's own auto-hide option. While this taskbar runs, the Windows
    /// one is hidden anyway; keeping it on auto-hide stops it from flashing at boot before we
    /// start. When the user exits on purpose, auto-hide is turned off so Windows' bar is back to normal.
    /// </summary>
    public static class WindowsTaskbarState
    {
        private const string StuckRectsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";
        private const int AutoHideByteIndex = 8;
        private const byte AutoHideOn = 0x03;
        private const byte AutoHideOff = 0x02;

        private const uint ABM_SETSTATE = 0x0A;
        private const int ABS_AUTOHIDE = 0x01;
        private const int ABS_ALWAYSONTOP = 0x02;

        public static void SetAutoHide(bool autoHide)
        {
            // Persisted setting, read by Explorer when it starts (e.g. at boot).
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(StuckRectsKey, true);
                if (key?.GetValue("Settings") is byte[] settings && settings.Length > AutoHideByteIndex)
                {
                    settings[AutoHideByteIndex] = autoHide ? AutoHideOn : AutoHideOff;
                    key.SetValue("Settings", settings, RegistryValueKind.Binary);
                }
            }
            catch (Exception e)
            {
                ShellLogger.Error($"WindowsTaskbarState: Unable to update StuckRects3: {e.Message}");
            }

            // Live setting, so the running Explorer taskbar changes right away.
            try
            {
                var data = new APPBARDATA
                {
                    cbSize = Marshal.SizeOf<APPBARDATA>(),
                    hWnd = FindWindow("Shell_TrayWnd", null),
                    lParam = (IntPtr)(autoHide ? ABS_AUTOHIDE : ABS_ALWAYSONTOP)
                };

                if (data.hWnd != IntPtr.Zero)
                {
                    SHAppBarMessage(ABM_SETSTATE, ref data);
                }
            }
            catch (Exception e)
            {
                ShellLogger.Error($"WindowsTaskbarState: Unable to set taskbar state: {e.Message}");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("shell32.dll")]
        private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    }
}

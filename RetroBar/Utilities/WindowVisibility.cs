using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Detects windows that are technically visible but can't be seen, such as fully
    /// transparent consoles used by tools to run hidden commands. The Windows taskbar
    /// doesn't show these, so neither should we.
    /// </summary>
    public static class WindowVisibility
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x80000;
        private const uint LWA_ALPHA = 0x2;

        public static bool IsInvisibleWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                return false;
            }

            if (GetLayeredWindowAttributes(hWnd, out _, out byte alpha, out uint flags))
            {
                // Layered with a constant alpha of zero: fully transparent.
                return (flags & LWA_ALPHA) != 0 && alpha == 0;
            }

            // Layered windows without attributes are drawn via UpdateLayeredWindow. That's
            // normal for some apps, but a console window should never be one; when it is,
            // it's a hidden console.
            var className = new StringBuilder(64);
            GetClassName(hWnd, className, className.Capacity);
            return className.ToString() == "ConsoleWindowClass";
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint crKey, out byte bAlpha, out uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace Guardian.Tray;

/// <summary>Win32 P/Invoke surface. Every caller wraps these in try/catch; nothing here throws on its own.</summary>
internal static class NativeMethods
{
    public const uint WM_CLOSE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        /// <summary>Tick count (32-bit, wraps every 49.7 days) of the last input event.</summary>
        public uint dwTime;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Window title, or "" when the window has none or the call fails.</summary>
    public static string GetWindowTitle(IntPtr hWnd)
    {
        try
        {
            var len = GetWindowTextLength(hWnd);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Seconds since the last keyboard/mouse input in this session, or 0 when unknown.</summary>
    public static int GetIdleSeconds()
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii)) return 0;
            // dwTime is the 32-bit tick count. Truncating TickCount64 to uint and subtracting in unsigned
            // arithmetic gives the right answer across the 49.7-day wrap-around.
            uint now = unchecked((uint)Environment.TickCount64);
            uint idleMs = unchecked(now - lii.dwTime);
            return (int)Math.Min(idleMs / 1000, int.MaxValue);
        }
        catch
        {
            return 0;
        }
    }
}

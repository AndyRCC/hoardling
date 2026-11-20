using System.Runtime.InteropServices;

namespace Hoardling.Windows;

internal static class NativeMethods
{
    public const int GwlExStyle = -20;
    public const long WsExToolWindow = 0x00000080;
    public const long WsExAppWindow = 0x00040000;
    public const long WsExNoActivate = 0x08000000;
    public const long WsExTransparent = 0x00000020;

    public const uint NimAdd = 0;
    public const uint NimModify = 1;
    public const uint NimDelete = 2;
    public const uint NifMessage = 0x1;
    public const uint NifIcon = 0x2;
    public const uint NifTip = 0x4;
    public const int WmApp = 0x8000;
    public const int WmLButtonUp = 0x0202;
    public const int WmRButtonUp = 0x0205;
    public const int WmContextMenu = 0x007B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconFromResourceEx(byte[] presbits, uint dwResSize, bool fIcon, uint dwVer, int cxDesired, int cyDesired, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>
    /// Work area of every monitor (taskbars excluded) in WPF device-independent pixels, primary first.
    /// The app is system-DPI aware, so physical pixels convert with the system DPI.
    /// </summary>
    public static List<(double X, double Y, double W, double H, bool Primary)> MonitorWorkAreas()
    {
        var scale = GetDpiForSystem() / 96.0;
        if (scale <= 0)
        {
            scale = 1;
        }
        var result = new List<(double, double, double, double, bool)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var w = info.rcWork;
                result.Add((w.Left / scale, w.Top / scale, (w.Right - w.Left) / scale, (w.Bottom - w.Top) / scale, (info.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        result.Sort((a, b) => b.Item5.CompareTo(a.Item5));
        return result;
    }

    public static void AddExStyle(IntPtr hwnd, long add, long remove = 0)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr((style | add) & ~remove));
    }
}

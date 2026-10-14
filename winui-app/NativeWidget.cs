using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Timetable.Desktop;

internal sealed class NativeWidget : IDisposable
{
    private const uint TrayMessage = 0x8000 + 91;
    private readonly nint hwnd;
    private readonly Action<int> action;
    private readonly Action displayChanged;
    private readonly Func<int> cursorEdges;
    private readonly SubclassProc callback;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private nint icon;
    private nint desktopOwner;
    private bool disposed, desktopMode, showingResizeCursor;
    public bool DesktopAttached => desktopOwner != 0 && IsWindow(desktopOwner) && GetWindow(hwnd, 4) == desktopOwner;
    public bool TrayAvailable { get; private set; }

    internal NativeWidget(nint hwnd, Action<int> action, Action displayChanged, Func<int> cursorEdges)
    {
        this.hwnd = hwnd; this.action = action; this.displayChanged = displayChanged; this.cursorEdges = cursorEdges;
        callback = WindowProc;
        if (!SetWindowSubclass(hwnd, callback, 1, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        icon = CreateCalendarIcon();
        AddTray();
    }

    public void AttachDesktop()
    {
        nint owner = 0;
        EnumWindows((candidate, _) =>
        {
            if (IsWindowVisible(candidate) && FindWindowEx(candidate, 0, "SHELLDLL_DefView", null) != 0)
            { owner = candidate; return false; }
            return true;
        }, 0);
        desktopMode = true;
        desktopOwner = owner;
        if (owner != 0) SetWindowLongPtr(hwnd, -8, owner);
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, (nint)((style | 0x08000000 | 0x80) & ~0x40000)); // NOACTIVATE | TOOLWINDOW
        LowerToDesktop();
    }

    private void LowerToDesktop() => SetWindowPos(hwnd, 1, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x200);

    public static PointInt32 Cursor() { GetCursorPos(out var point); return point; }
    internal static void MoveCursor(PointInt32 point)
    {
        // A hardware-style move updates WinUI's pointer pipeline. SetCursorPos
        // alone moves the image without reliably producing WM_POINTER updates.
        var input = new MouseInputEvent { Type = 0, Mouse = new MouseInput
        {
            X = (int)Math.Round((point.X - GetSystemMetrics(76)) * 65535.0 / Math.Max(1, GetSystemMetrics(78) - 1)),
            Y = (int)Math.Round((point.Y - GetSystemMetrics(77)) * 65535.0 / Math.Max(1, GetSystemMetrics(79) - 1)),
            Flags = 0xE001
        } };
        if (SendInput(1, [input], Marshal.SizeOf<MouseInputEvent>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static bool CursorMatches(int resource)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && info.Cursor == LoadCursor(0, (nint)resource);
    }
    internal static string CursorDescription(int resource)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        GetCursorInfo(ref info);
        return $"actual={info.Cursor}, expected={LoadCursor(0, (nint)resource)}, arrow={LoadCursor(0, 32512)}, position={info.Position.X},{info.Position.Y}, hit={GetAncestor(WindowFromPoint(info.Position), 2)}";
    }
    public static uint Dpi(nint hwnd) => GetDpiForWindow(hwnd);
    public static void RemoveWindowFrame(nint hwnd)
    {
        // The widget draws its own rounded surface and handles resizing itself.
        var style = GetWindowLongPtr(hwnd, -16).ToInt64();
        SetWindowLongPtr(hwnd, -16, (nint)(style & ~(0x00C00000L | 0x00040000L)));
        var noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int)); // DWMWA_BORDER_COLOR
        var square = 1;
        DwmSetWindowAttribute(hwnd, 33, ref square, sizeof(int)); // XAML owns the corner radius
        var margins = new FrameMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        // Enable composition alpha over the desktop, with an empty blur region.
        var region = CreateRectRgn(-1, -1, -1, -1);
        try
        {
            var blur = new BlurBehind { Flags = 3, Enable = 1, Region = region };
            DwmEnableBlurBehindWindow(hwnd, ref blur);
        }
        finally { DeleteObject(region); }
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x1 | 0x2 | 0x4 | 0x10 | 0x20);
    }
    public static void BringToFront(nint hwnd) { ShowWindow(hwnd, 9); SetForegroundWindow(hwnd); }
    internal static void RaiseForDiagnostic(nint hwnd) => SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);

    private NotifyIconData TrayData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(), hWnd = hwnd, uID = 1,
        uFlags = 1 | 2 | 4, uCallbackMessage = TrayMessage, hIcon = icon,
        szTip = "桌面课表 · 右键设置", szInfo = "", szInfoTitle = ""
    };

    private void AddTray() { var data = TrayData(); TrayAvailable = Shell_NotifyIcon(0, ref data); }

    private nint WindowProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint reference)
    {
        if (message == 0x20) // WM_SETCURSOR: also covers WinUI's non-client input regions.
        {
            var resource = cursorEdges() switch { 1 or 2 => 32644, 4 or 8 => 32645,
                5 or 10 => 32642, 6 or 9 => 32643, _ => 0 };
            if (resource != 0 || showingResizeCursor)
            {
                showingResizeCursor = resource != 0;
                SetCursor(LoadCursor(0, (nint)(resource == 0 ? 32512 : resource)));
                return 1;
            }
        }
        if (desktopMode)
        {
            if (message == 0x21) return 3; // WM_MOUSEACTIVATE: MA_NOACTIVATE
            if (message == 0x112 && (wParam.ToInt64() & 0xfff0) == 0xf020) return 0; // SC_MINIMIZE
            if (message == 0x46 && lParam != 0) // WM_WINDOWPOSCHANGING
            {
                var position = Marshal.PtrToStructure<WindowPosition>(lParam);
                if ((position.Flags & 0x4) == 0) position.After = 1; // Always HWND_BOTTOM, including activation requests.
                position.Flags |= 0x10 | 0x200; // NOACTIVATE | NOOWNERZORDER
                // Show Desktop must not hide this desktop-owned tool window.
                if ((position.Flags & 0x80) != 0) position.Flags = (position.Flags & ~0x80u) | 0x40;
                Marshal.StructureToPtr(position, lParam, false);
            }
        }
        if (message == taskbarCreated) { AddTray(); AttachDesktop(); }
        else if (message == 0x7E) displayChanged();
        else if (message == TrayMessage)
        {
            if ((int)lParam == 0x202) { ShowWindow(hwnd, 4); LowerToDesktop(); }
            else if ((int)lParam == 0x205) ShowTrayMenu();
        }
        else if (message == 0x111)
        {
            var command = (int)(wParam.ToInt64() & 0xffff);
            if (command is >= 1001 and <= 1016) action(command);
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowTrayMenu()
    {
        var menu = CreatePopupMenu();
        var preview = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 1001, "编辑课表");
            AppendMenu(menu, 0, 1003, "调整时间(测试用)");
            AppendMenu(menu, 0, 1005, "锁定 / 解锁位置和大小");
            AppendMenu(preview, 0, 1009, "自动显示今天");
            for (var day = 0; day < 7; day++) AppendMenu(preview, 0, 1010 + day, DailyNames[day]);
            AppendMenu(menu, 0x10, preview, "查看课表");
            AppendMenu(menu, 0, 1006, "恢复默认位置和大小");
            AppendMenu(menu, 0, 1002, "重新读取课表");
            AppendMenu(menu, 0, 1007, "检查更新");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 1004, "退出");
            SetForegroundWindow(hwnd);
            var cursor = Cursor();
            var command = TrackPopupMenu(menu, 0x2 | 0x100, cursor.X, cursor.Y, 0, hwnd, 0);
            PostMessage(hwnd, 0, 0, 0);
            if (command != 0) PostMessage(hwnd, 0x111, command, 0);
        }
        finally { DestroyMenu(menu); }
        LowerToDesktop();
    }

    internal static readonly string[] DailyNames = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日"];

    private nint CreateCalendarIcon()
    {
        // The same white calendar and dark fill as the Python tray, embedded in
        // the EXE with five resolutions so copying the app needs no icon file.
        using var stream = typeof(NativeWidget).Assembly.GetManifestResourceStream("Timetable.CalendarIcon")
            ?? throw new InvalidDataException("日历图标资源缺失");
        using var reader = new BinaryReader(stream);
        reader.ReadUInt16(); reader.ReadUInt16();
        var count = reader.ReadUInt16();
        var size = Math.Max(16, GetSystemMetricsForDpi(49, Dpi(hwnd))); // SM_CXSMICON
        var difference = int.MaxValue;
        uint length = 0, offset = 0;
        for (var index = 0; index < count; index++)
        {
            var width = reader.ReadByte(); reader.ReadByte(); reader.ReadByte(); reader.ReadByte();
            reader.ReadUInt16(); reader.ReadUInt16();
            var bytes = reader.ReadUInt32(); var position = reader.ReadUInt32();
            if (Math.Abs(width - size) < difference)
            { difference = Math.Abs(width - size); length = bytes; offset = position; }
        }
        stream.Position = offset;
        var result = CreateIconFromResourceEx(reader.ReadBytes(checked((int)length)), length, true, 0x00030000, size, size, 0);
        if (result == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return result;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        desktopMode = false;
        var data = TrayData(); Shell_NotifyIcon(2, ref data);
        RemoveWindowSubclass(hwnd, callback, 1);
        if (icon != 0) { DestroyIcon(icon); icon = 0; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize; public nint hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState; public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint reference);
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public nint Window, After; public int X, Y, Width, Height; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public int Size; public uint Flags; public nint Cursor; public PointInt32 Position; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputEvent { public uint Type; public MouseInput Mouse; }
    private delegate bool EnumProc(nint hwnd, nint lParam);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointInt32 point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, MouseInputEvent[] input, int size);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] private static extern nint LoadCursor(nint instance, nint resource);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointInt32 point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flag);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint child, string name, string? title);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateIconFromResourceEx(byte[] bytes, uint length, bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nint id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct FrameMargins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BlurBehind { public uint Flags; public int Enable; public nint Region; public int Transition; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref FrameMargins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint hwnd, ref BlurBehind blur);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}

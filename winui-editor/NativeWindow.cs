using System.Runtime.InteropServices;

namespace Timetable.Editor;

internal static class NativeWindow
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    internal static uint Dpi(nint hwnd) => GetDpiForWindow(hwnd);
    internal static void BringToFront(nint hwnd) { ShowWindow(hwnd, 9); SetForegroundWindow(hwnd); }
}

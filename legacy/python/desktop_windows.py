"""Desktop-owned bottom window and physical-pixel geometry for Qt painting."""

import ctypes
import sys
from ctypes import wintypes as W

IS_WINDOWS = sys.platform == "win32"


def enable_dpi_awareness():
    # Must happen before Qt creates any HWND. The manifest covers frozen builds.
    if not IS_WINDOWS:
        return
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    try:
        fn = user32.SetProcessDpiAwarenessContext
        fn.argtypes = [W.HANDLE]
        fn.restype = W.BOOL
        if fn(ctypes.c_void_p(-4)):  # DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            return
    except AttributeError:
        pass
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except (OSError, AttributeError):
        user32.SetProcessDPIAware()


if IS_WINDOWS:
    U = ctypes.WinDLL("user32", use_last_error=True)
    ENUMPROC = ctypes.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)

    class MonitorInfo(ctypes.Structure):
        _fields_ = [("cbSize", W.DWORD), ("rcMonitor", W.RECT),
                    ("rcWork", W.RECT), ("dwFlags", W.DWORD), ("szDevice", W.WCHAR * 32)]

    def bind(name, args, result):
        fn = getattr(U, name)
        fn.argtypes, fn.restype = args, result
        return fn

    FindWindow = bind("FindWindowW", [W.LPCWSTR, W.LPCWSTR], W.HWND)
    FindWindowEx = bind("FindWindowExW", [W.HWND, W.HWND, W.LPCWSTR, W.LPCWSTR], W.HWND)
    EnumWindows = bind("EnumWindows", [ENUMPROC, W.LPARAM], W.BOOL)
    SendMessageTimeout = bind("SendMessageTimeoutW", [W.HWND, W.UINT, W.WPARAM, W.LPARAM,
        W.UINT, W.UINT, ctypes.POINTER(ctypes.c_size_t)], ctypes.c_ssize_t)
    SetParent = bind("SetParent", [W.HWND, W.HWND], W.HWND)
    GetParent = bind("GetParent", [W.HWND], W.HWND)
    GetWindow = bind("GetWindow", [W.HWND, W.UINT], W.HWND)
    IsWindow = bind("IsWindow", [W.HWND], W.BOOL)
    IsWindowVisible = bind("IsWindowVisible", [W.HWND], W.BOOL)
    GetWindowRect = bind("GetWindowRect", [W.HWND, ctypes.POINTER(W.RECT)], W.BOOL)
    ScreenToClient = bind("ScreenToClient", [W.HWND, ctypes.POINTER(W.POINT)], W.BOOL)
    GetCursorPos = bind("GetCursorPos", [ctypes.POINTER(W.POINT)], W.BOOL)
    SetWindowPos = bind("SetWindowPos", [W.HWND, W.HWND, ctypes.c_int, ctypes.c_int,
        ctypes.c_int, ctypes.c_int, W.UINT], W.BOOL)
    suffix = "PtrW" if ctypes.sizeof(ctypes.c_void_p) == 8 else "W"
    GetStyle = bind("GetWindowLong" + suffix, [W.HWND, ctypes.c_int], ctypes.c_ssize_t)
    SetStyle = bind("SetWindowLong" + suffix, [W.HWND, ctypes.c_int, ctypes.c_ssize_t], ctypes.c_ssize_t)
    MonitorFromPoint = bind("MonitorFromPoint", [W.POINT, W.DWORD], W.HANDLE)
    GetMonitorInfo = bind("GetMonitorInfoW", [W.HANDLE, ctypes.POINTER(MonitorInfo)], W.BOOL)


def desktop_host():
    # Use the icon host as OWNER, never the wallpaper WorkerW as a child parent.
    candidates = []

    @ENUMPROC
    def visit(hwnd, _):
        if FindWindowEx(hwnd, None, "SHELLDLL_DefView", None):
            if IsWindowVisible(hwnd):
                candidates.append(hwnd)
        return True

    EnumWindows(visit, 0)
    if candidates:
        return candidates[0]
    return None


class DesktopBridge:
    def __init__(self, widget, enabled=True):
        self.widget = widget
        self.enabled = IS_WINDOWS and enabled
        self.parent = None
        self.error = ""

    @property
    def hwnd(self):
        return int(self.widget.winId())

    def attach(self):
        if not self.enabled:
            return False
        parent = desktop_host()
        if not parent:
            self.error = "未找到 Windows 桌面窗口"
            return False
        hwnd = self.hwnd
        # Qt's translucent top-level window must remain top-level. Reparenting
        # its HWND with SetParent bypasses Qt's platform-window bookkeeping and
        # can produce a live but invisible layered window. A shell-owned popup
        # stays above its desktop owner without becoming a child window.
        ctypes.set_last_error(0)
        previous = SetStyle(hwnd, -8, parent)  # GWLP_HWNDPARENT = owner for a popup
        if not previous and ctypes.get_last_error():
            self.error = str(ctypes.WinError(ctypes.get_last_error()))
            return False
        self.parent = parent
        ex = GetStyle(hwnd, -20)
        SetStyle(hwnd, -20, (ex | 0x08000000 | 0x80) & ~0x40000)  # NOACTIVATE, TOOLWINDOW
        # HWND_BOTTOM also removes topmost status, while Win32 keeps an owned
        # window above its owner. Normal application windows remain above us.
        SetWindowPos(hwnd, W.HWND(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0020 | 0x0040)
        self.error = ""
        return True

    def attached(self):
        return bool(self.enabled and self.parent and IsWindow(self.parent)
                    and GetWindow(self.hwnd, 4) == self.parent)  # GW_OWNER

    def cursor(self):
        if self.enabled:
            point = W.POINT()
            GetCursorPos(ctypes.byref(point))
            return point.x, point.y
        from PyQt5.QtGui import QCursor
        point = QCursor.pos()
        return point.x(), point.y()

    def work_area(self, point=None):
        if self.enabled:
            point = point or self.cursor()
            info = MonitorInfo()
            info.cbSize = ctypes.sizeof(info)
            monitor = MonitorFromPoint(W.POINT(*map(int, point)), 2)
            if not GetMonitorInfo(monitor, ctypes.byref(info)):
                raise ctypes.WinError(ctypes.get_last_error())
            r = info.rcWork
            return (r.left, r.top, r.right-r.left, r.bottom-r.top), info.szDevice
        from PyQt5.QtWidgets import QApplication
        from PyQt5.QtCore import QPoint
        screen = QApplication.screenAt(QPoint(*point)) if point else self.widget.screen()
        screen = screen or QApplication.primaryScreen()
        r = screen.availableGeometry()
        return (r.x(), r.y(), r.width(), r.height()), screen.name()

    def rect(self):
        if self.enabled:
            r = W.RECT()
            GetWindowRect(self.hwnd, ctypes.byref(r))
            return r.left, r.top, r.right-r.left, r.bottom-r.top
        r = self.widget.geometry()
        return r.x(), r.y(), r.width(), r.height()

    def set_rect(self, rect):
        x, y, w, h = map(round, rect)
        if self.enabled:
            previous = self.rect()
            if previous == (x, y, w, h):
                return
            # Ownership / bottom ordering is established in attach(). Moving
            # must not repeatedly reinsert/show the layered window in Explorer's
            # z-order; that causes unnecessary desktop composition and flicker.
            flags = 0x0004 | 0x0010 | 0x0200  # NOZORDER, NOACTIVATE, NOOWNERZORDER
            if previous[2:] == (w, h):
                flags |= 0x0001  # NOSIZE: preserve backing store on pure movement
            if previous[:2] == (x, y):
                flags |= 0x0002  # NOMOVE
            if not SetWindowPos(self.hwnd, None, x, y, w, h, flags):
                raise ctypes.WinError(ctypes.get_last_error())
        else:
            self.widget.setGeometry(x, y, w, h)

    def initial_work_area(self, screen_name=None):
        from PyQt5.QtWidgets import QApplication
        screens = QApplication.screens()
        screen = next((s for s in screens if s.name() == screen_name), QApplication.primaryScreen())
        if not self.enabled:
            r = screen.availableGeometry()
            return (r.x(), r.y(), r.width(), r.height()), screen.name()
        # Qt's monitor origin is native on Windows; sizes are logical. A point
        # just inside the origin locates the native monitor without mixing sizes.
        origin = screen.geometry().topLeft()
        return self.work_area((origin.x() + 1, origin.y() + 1))

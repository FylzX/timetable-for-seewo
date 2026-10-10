import sys
import unittest
from unittest.mock import patch

import desktop_windows as desktop


@unittest.skipUnless(sys.platform == "win32", "Win32 desktop host selection")
class DesktopHostTests(unittest.TestCase):
    def test_uses_icon_host_not_wallpaper_worker(self):
        def enumerate_windows(callback, parameter):
            for hwnd in (100, 200, 300):
                callback(hwnd, parameter)
            return True

        # 100 = icon host, 200 = wallpaper WorkerW, 300 = unrelated window.
        def find_child(parent, after, cls, title):
            return 101 if parent == 100 and cls == "SHELLDLL_DefView" else 0

        with patch.object(desktop, "EnumWindows", side_effect=enumerate_windows), \
             patch.object(desktop, "FindWindowEx", side_effect=find_child), \
             patch.object(desktop, "IsWindowVisible", return_value=True):
            self.assertEqual(desktop.desktop_host(), 100)

    def test_missing_shell_does_not_select_an_unrelated_worker(self):
        with patch.object(desktop, "EnumWindows", return_value=True):
            self.assertIsNone(desktop.desktop_host())

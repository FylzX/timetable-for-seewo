import os
os.environ["QT_QPA_PLATFORM"] = "offscreen"

import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch

from PyQt5.QtCore import QCoreApplication
from PyQt5.QtWidgets import QApplication

from app import make_application, settings_directory
from winui_bridge import NativeEditor, editor_executable


class BridgeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or make_application([])

    def test_portable_layout_does_not_depend_on_appdata(self):
        with patch("app.data_directory", return_value=Path("D:/Portable/Timetable")):
            self.assertEqual(settings_directory(), Path("D:/Portable/Timetable/user-data"))

    def test_finds_development_and_portable_editor(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            self.assertIsNone(editor_executable(root))
            dev = root / "winui-editor/bin/Debug/net8.0-windows10.0.19041.0/win-x64/Timetable.Editor.exe"
            dev.parent.mkdir(parents=True)
            dev.touch()
            self.assertEqual(editor_executable(root), dev)
            portable = root / "editor/Timetable.Editor.exe"
            portable.parent.mkdir()
            portable.touch()
            self.assertEqual(editor_executable(root), portable)

    def test_protocol_handles_partial_lines_and_notifies_save_and_cancel(self):
        editor = NativeEditor(Path.cwd())
        saved, cancelled = [], []
        editor.saved.connect(lambda: saved.append(True))
        editor.closeCancelled.connect(lambda: cancelled.append(True))
        editor.buffer = b'{"type":"dirty","dirty":true}\n{"type":"saved"'
        editor.read_messages()
        self.assertTrue(editor.dirty)
        self.assertEqual(saved, [])
        editor.buffer += b'}\n{"type":"closeCancelled"}\ninvalid line\n'
        editor.read_messages()
        self.assertFalse(editor.dirty)
        self.assertEqual(saved, [True])
        self.assertEqual(cancelled, [True])
        editor.deleteLater()
        QCoreApplication.processEvents()


if __name__ == "__main__":
    unittest.main()

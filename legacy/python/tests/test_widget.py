import os
os.environ["QT_QPA_PLATFORM"] = "offscreen"
if os.name == "nt":
    # The offscreen plugin does not discover Windows fonts automatically.
    os.environ["QT_QPA_FONTDIR"] = os.path.join(os.environ.get("WINDIR", "C:/Windows"), "Fonts")

import unittest
from datetime import date, datetime
from unittest.mock import patch

from app import TimetableWidget, make_application
from PyQt5.QtCore import QPoint, Qt
from PyQt5.QtTest import QTest
from PyQt5.QtWidgets import QApplication


class WidgetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or make_application([])

    def setUp(self):
        self.widget = TimetableWidget(desktop=False)
        self.widget.show()
        self.app.processEvents()

    def tearDown(self):
        self.widget.close()
        self.widget.deleteLater()
        self.app.processEvents()

    def test_lock_blocks_drag_and_resize_and_can_unlock(self):
        w = self.widget
        QTest.mouseClick(w, Qt.LeftButton, pos=w.lock_rect().center().toPoint())
        self.assertTrue(w.locked)
        QTest.mousePress(w, Qt.LeftButton, pos=QPoint(2, 2))
        self.assertIsNone(w.gesture)
        QTest.mouseRelease(w, Qt.LeftButton, pos=QPoint(2, 2))
        QTest.mouseClick(w, Qt.LeftButton, pos=w.lock_rect().center().toPoint())
        self.assertFalse(w.locked)
        QTest.mousePress(w, Qt.LeftButton, pos=QPoint(2, 2))
        self.assertEqual(w.gesture[0], "tl")
        QTest.mouseRelease(w, Qt.LeftButton, pos=QPoint(2, 2))

    def test_midnight_refresh_changes_header_and_courses(self):
        w = self.widget
        w.current_date = date(2026, 9, 15)
        with patch("app.date") as mocked:
            mocked.today.return_value = date(2026, 9, 16)
            w.refresh()
        self.assertEqual(w.shown_date, date(2026, 9, 16))
        self.assertEqual(len(w.rows), 13)

    def test_all_days_paint_at_different_sizes(self):
        for day in range(7):
            self.widget.set_preview(day)
            for width, height in ((300, 480), (380, 632), (680, 900)):
                self.widget.resize(width, height)
                self.app.processEvents()
                self.assertFalse(self.widget.grab().isNull())

    def test_sunday_exception_rows_and_highlight(self):
        w = self.widget
        w.current_date = date(2026, 9, 20)
        w.update_marker(datetime(2026, 9, 20, 19, 15))
        self.assertEqual(w.shown_date.weekday(), 6)
        self.assertEqual(len(w.rows), 12)
        self.assertEqual(w.rows[10].course, "英语")
        self.assertEqual(w.marker, (10, "live"))
        self.assertFalse(w.grab().isNull())

    def test_marker_changes_without_moving_window_and_stops_after_school(self):
        w = self.widget
        w.current_date = date(2026, 9, 15)
        original = w.geometry()
        w.update_marker(datetime(2026, 9, 15, 13, 5))
        self.assertEqual(w.marker, (6, "next"))
        self.assertFalse(w.grab().isNull())
        self.assertEqual(w.geometry(), original)
        w.update_marker(datetime(2026, 9, 15, 13, 15))
        self.assertEqual(w.marker, (6, "live"))
        w.update_marker(datetime(2026, 9, 15, 21, 15))
        self.assertIsNone(w.marker)



if __name__ == "__main__":
    unittest.main()

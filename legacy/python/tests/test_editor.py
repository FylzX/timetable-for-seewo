import os
os.environ["QT_QPA_PLATFORM"] = "offscreen"
if os.name == "nt":
    os.environ["QT_QPA_FONTDIR"] = os.path.join(os.environ.get("WINDIR", "C:/Windows"), "Fonts")

from copy import deepcopy
from datetime import date, datetime
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from PyQt5.QtCore import QDate, QPointF, QTime, Qt
from PyQt5.QtTest import QTest
from PyQt5.QtWidgets import QApplication, QMessageBox

from app import make_application, TimetableWidget
from editor import EditorDialog, TimeRangeDialog, AddDateDialog
from editor_data import EditorDraft, ScheduleStore
from schedule import load_schedule, lessons_for
from test_clock import SimulationClock, TimeTestDialog


def fixture():
    return {"weekly": {str(day): [{"start": "08:00", "end": "08:40", "course": "语文"}]
                       for day in range(1, 6)},
            "exceptions": {"2026-10-12": []},
            "template": [{"start": "09:00", "end": "09:40"}]}


class EditorDataTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.addCleanup(load_schedule)
        self.directory = Path(self.temp.name)
        data = fixture()
        for filename, key in (("timetable.json", "weekly"), ("exceptions.json", "exceptions"), ("template.json", "template")):
            (self.directory / filename).write_text(json.dumps(data[key]), encoding="utf-8")
        self.store = ScheduleStore(self.directory)

    def test_save_persists_shared_template_and_updates_runtime(self):
        draft = EditorDraft(self.store.snapshot())
        draft.data["weekly"]["1"][0]["course"] = "数学"
        draft.data["template"].append({"start": "10:00", "end": "10:40"})
        self.store.save(draft.data)
        self.assertEqual(lessons_for(date(2026, 10, 12)), ())
        self.assertEqual(lessons_for(date(2026, 10, 19))[0].course, "数学")
        self.assertEqual(ScheduleStore(self.directory).snapshot(), draft.data)

    def test_invalid_or_incomplete_save_keeps_disk_and_runtime(self):
        original = {p: p.read_bytes() for p in self.directory.iterdir()}
        data = self.store.snapshot()
        data["weekly"]["1"][0]["course"] = ""
        with self.assertRaises(ValueError):
            self.store.save(data)
        self.assertEqual({p: p.read_bytes() for p in self.directory.iterdir()}, original)
        self.assertEqual(self.store.snapshot(), fixture())

    def test_write_failure_rolls_back_preceding_file(self):
        import editor_data
        original = {p: p.read_bytes() for p in self.directory.iterdir()}
        data = self.store.snapshot()
        data["weekly"]["1"][0]["course"] = "数学"
        real = editor_data._replace_bytes
        calls = []

        def fail_second(path, content):
            calls.append(path)
            if len(calls) == 2:
                raise PermissionError("simulated failure")
            real(path, content)

        with patch("editor_data._replace_bytes", side_effect=fail_second):
            with self.assertRaises(OSError):
                self.store.save(data)
        self.assertEqual({p: p.read_bytes() for p in self.directory.iterdir()}, original)
        self.assertEqual(self.store.snapshot(), fixture())

    def test_copy_date_and_template_are_independent(self):
        draft = EditorDraft(fixture())
        draft.add_date(date(2026, 10, 13), 1)
        draft.data["exceptions"]["2026-10-13"][0]["course"] = "物理"
        self.assertEqual(draft.data["weekly"]["1"][0]["course"], "语文")
        draft.apply_template("weekly", ["1", "2"])
        self.assertEqual(draft.data["weekly"]["1"], [{"start": "09:00", "end": "09:40", "course": ""}])
        draft.data["weekly"]["1"][0]["start"] = "09:10"
        self.assertEqual(draft.data["weekly"]["2"][0]["start"], "09:00")
        self.assertEqual(draft.data["template"][0]["start"], "09:00")
        self.assertEqual(list(draft.data["exceptions"])[-1], "2026-10-13")


class EditorWidgetTests(EditorDataTests):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or make_application([])

    def setUp(self):
        super().setUp()
        self.editor = EditorDialog(self.store)
        self.editor.resize(1260, 840)
        self.editor.show()
        self.app.processEvents()

    def tearDown(self):
        self.editor._approved_close = True
        self.editor.close()
        self.editor.deleteLater()
        self.app.processEvents()

    def test_overwrite_cancel_is_atomic_for_all_days(self):
        before = deepcopy(self.editor.draft.data)
        with patch("editor.confirm", return_value=False):
            self.assertFalse(self.editor.apply_template("weekly", ["1", "2", "3", "4", "5"]))
        self.assertEqual(self.editor.draft.data, before)
        with patch("editor.confirm", return_value=True):
            self.assertTrue(self.editor.apply_template("weekly", ["1", "2", "3", "4", "5"]))
        for day in range(1, 6):
            self.assertEqual(self.editor.draft.data["weekly"][str(day)][0]["course"], "")

    def test_manual_add_accepts_independent_time_and_dropdown(self):
        with patch.object(TimeRangeDialog, "exec_", return_value=TimeRangeDialog.Accepted):
            self.editor.columns["weekly"]["1"].add_lesson()
        rows = self.editor.draft.data["weekly"]["1"]
        self.assertEqual(rows[-1]["start"], "08:50")
        self.assertEqual(self.editor.draft.data["template"][0]["start"], "09:00")
        from editor import LessonCard
        cards = self.editor.columns["weekly"]["1"].findChildren(LessonCard)
        card = next(card for card in cards if card.row is rows[-1])
        card.course.setCurrentIndex(card.course.findData("物理"))
        self.assertEqual(rows[-1]["course"], "物理")
        card.course.setCurrentIndex(card.course.count() - 1)
        card.custom.setText("班会")
        self.assertEqual(rows[-1]["course"], "班会")

    def test_time_dialog_rejects_overlap_and_accepts_adjacent(self):
        rows = fixture()["weekly"]["1"]
        dialog = TimeRangeDialog("08:20", "09:00", rows)
        dialog.check_and_accept()
        self.assertNotEqual(dialog.result(), dialog.Accepted)
        self.assertIn("重叠", dialog.error.text())
        dialog.start.setTime(QTime(8, 40))
        dialog.check_and_accept()
        self.assertEqual(dialog.result(), dialog.Accepted)

    def test_new_date_copies_weekday_and_is_added_on_right(self):
        def add(dialog):
            dialog.day.setDate(QDate(2026, 10, 15))
            dialog.source.setCurrentIndex(dialog.source.findData(2))
            return dialog.Accepted

        with patch.object(AddDateDialog, "exec_", new=add):
            self.editor.add_date()
        keys = list(self.editor.columns["exceptions"])
        self.assertEqual(keys[-1], "2026-10-15")
        self.assertEqual(self.editor.draft.data["exceptions"][keys[-1]], self.editor.draft.data["weekly"]["2"])
        self.editor.draft.data["exceptions"][keys[-1]][0]["course"] = "化学"
        self.assertEqual(self.editor.draft.data["weekly"]["2"][0]["course"], "语文")

    def test_close_save_writes_data_before_closing(self):
        self.editor.draft.data["weekly"]["1"][0]["course"] = "英语"

        def choose_save(box):
            next(b for b in box.buttons() if b.text() == "保存并关闭").click()
            return 0

        with patch.object(QMessageBox, "exec_", new=choose_save):
            self.editor.close()
            QTest.qWait(300)
        self.assertFalse(self.editor.isVisible())
        self.assertEqual(ScheduleStore(self.directory).data["weekly"]["1"][0]["course"], "英语")

    def test_date_delete_requires_confirmation(self):
        with patch("editor.confirm", return_value=False):
            self.assertFalse(self.editor.delete_date("2026-10-12"))
        self.assertIn("2026-10-12", self.editor.draft.data["exceptions"])
        with patch("editor.confirm", return_value=True):
            self.assertTrue(self.editor.delete_date("2026-10-12"))
        self.assertNotIn("2026-10-12", self.editor.draft.data["exceptions"])
        self.assertFalse(self.editor.template_panels["exceptions"].apply_button.isEnabled())

    def test_save_button_clears_dirty_and_emits_refresh(self):
        emitted = []
        self.editor.saved.connect(lambda: emitted.append(True))
        self.editor.draft.data["weekly"]["1"][0]["course"] = "数学"
        self.editor.changed()
        self.assertTrue(self.editor.save_button.isEnabled())
        self.assertTrue(self.editor.save_changes())
        self.assertFalse(self.editor.draft.dirty)
        self.assertEqual(emitted, [True])
        self.assertFalse(self.editor.save_button.isEnabled())

    def test_close_shakes_then_can_keep_editing_or_discard(self):
        self.editor.draft.data["weekly"]["1"][0]["course"] = "数学"
        origin = self.editor.pos()

        def choose_keep(box):
            next(b for b in box.buttons() if b.text() == "继续编辑").click()
            return 0

        with patch.object(QMessageBox, "exec_", new=choose_keep):
            self.editor.close()
            QTest.qWait(300)
        self.assertTrue(self.editor.isVisible())
        self.assertEqual(self.editor.pos(), origin)
        self.assertTrue(self.editor.draft.dirty)

        def choose_discard(box):
            next(b for b in box.buttons() if b.text() == "放弃修改").click()
            return 0

        with patch.object(QMessageBox, "exec_", new=choose_discard):
            self.editor.close()
            QTest.qWait(300)
        self.assertFalse(self.editor.isVisible())
        self.assertEqual(self.store.data["weekly"]["1"][0]["course"], "语文")


class ClockTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or make_application([])

    def tearDown(self):
        load_schedule()

    def test_simulation_advances_monotonically_across_midnight(self):
        clock = SimulationClock()
        with patch("test_clock.time.monotonic", return_value=100):
            clock.set(datetime(2026, 10, 12, 23, 59, 59))
        with patch("test_clock.time.monotonic", return_value=102):
            self.assertEqual(clock.now(), datetime(2026, 10, 13, 0, 0, 1))
        clock.reset()
        self.assertFalse(clock.active)

    def test_date_time_applies_to_widget_and_reset_restores_system(self):
        w = TimetableWidget(desktop=False)
        window = TimeTestDialog(w.clock)
        window.changed.connect(w.time_changed)
        window.day.setDate(QDate(2026, 9, 20))
        window.time.setTime(QTime(19, 15))
        window.apply_time()
        self.assertEqual(w.shown_date, date(2026, 9, 20))
        self.assertEqual(w.marker, (10, "live"))
        window.reset_time()
        self.assertFalse(w.clock.active)
        self.assertEqual(w.shown_date, date.today())
        window.close()
        window.deleteLater()
        w.close()
        w.deleteLater()

    def test_dial_drag_changes_minutes_and_hours(self):
        from test_clock import ClockDial
        dial = ClockDial()
        dial.resize(260, 260)
        dial.set_time(QTime(3, 0))
        dial.show()
        self.app.processEvents()
        QTest.mousePress(dial, Qt.LeftButton, pos=QPointF(130, 45).toPoint())
        self.assertEqual(dial.hand, "minute")
        from PyQt5.QtGui import QMouseEvent
        event = QMouseEvent(QMouseEvent.MouseMove, QPointF(215, 130), Qt.NoButton, Qt.LeftButton, Qt.NoModifier)
        dial.mouseMoveEvent(event)
        QTest.mouseRelease(dial, Qt.LeftButton, pos=QPointF(215, 130).toPoint())
        self.assertEqual(dial.value, QTime(3, 15))
        dial.set_time(QTime(3, 0))
        QTest.mousePress(dial, Qt.LeftButton, pos=QPointF(180, 130).toPoint())
        self.assertEqual(dial.hand, "hour")
        event = QMouseEvent(QMouseEvent.MouseMove, QPointF(130, 180), Qt.NoButton, Qt.LeftButton, Qt.NoModifier)
        dial.mouseMoveEvent(event)
        QTest.mouseRelease(dial, Qt.LeftButton, pos=QPointF(130, 180).toPoint())
        self.assertEqual(dial.value, QTime(6, 0))
        dial.close()


if __name__ == "__main__":
    unittest.main()

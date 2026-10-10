import unittest
import json
import tempfile
from pathlib import Path
from unittest.mock import patch
from datetime import date, datetime

from schedule import SCHEDULE, lessons_for, lesson_marker, load_schedule, data_directory
from placement import capture_rect, restore_rect, clean_state


class ScheduleTests(unittest.TestCase):
    def setUp(self):
        load_schedule()

    def test_marker_boundaries_and_day_specific_breaks(self):
        day = date(2026, 9, 15)
        for hour, minute, expected in [
            (7, 0, (0, "next")), (7, 15, (0, "live")),
            (7, 45, (1, "next")), (7, 50, (1, "live")),
            (13, 5, (6, "next")), (13, 15, (6, "live")),
            (14, 15, (7, "live")), (15, 45, (9, "next")),
            (21, 15, None), (23, 0, None),
        ]:
            self.assertEqual(lesson_marker(day, datetime(2026, 9, 15, hour, minute)), expected)
        friday = date(2026, 9, 18)
        self.assertEqual(lesson_marker(friday, datetime(2026, 9, 18, 13, 35)), (6, "next"))
        self.assertIsNone(lesson_marker(friday, datetime(2026, 9, 18, 16, 0)))
        self.assertIsNone(lesson_marker(date(2026, 9, 19), datetime(2026, 9, 19, 8)))
        self.assertIsNone(lesson_marker(day, datetime(2026, 9, 16, 8)))

    def test_all_days_are_chronological_without_overlap(self):
        for day, rows in SCHEDULE.items():
            previous = "00:00"
            for row in rows:
                self.assertLess(row.start, row.end, day)
                self.assertGreaterEqual(row.start, previous, day)
                previous = row.end

    def test_cell_specific_times(self):
        self.assertEqual(SCHEDULE[0][0].time_label, "07:10–07:45")
        self.assertEqual([(r.start, r.end) for r in SCHEDULE[4][5:]], [
            ("12:50", "13:30"), ("13:40", "14:20"),
            ("14:30", "15:10"), ("15:20", "16:00")])
        self.assertEqual([(r.start, r.end) for r in SCHEDULE[2][-4:]], [
            ("18:00", "18:40"), ("18:50", "19:30"),
            ("19:40", "20:20"), ("20:30", "21:15")])
        self.assertEqual(SCHEDULE[1][5].end, "13:05")
        self.assertEqual(SCHEDULE[1][6].start, "13:15")
        self.assertEqual(SCHEDULE[1][8].end, "15:45")
        self.assertEqual(SCHEDULE[1][9].start, "15:55")
        self.assertEqual(SCHEDULE[3][-1].time_label, "20:25–21:15")

    def test_local_date_and_weekend(self):
        self.assertEqual(lessons_for(date(2026, 9, 15)), SCHEDULE[1])
        self.assertEqual(lessons_for(date(2026, 9, 19)), ())
        self.assertEqual(lessons_for(date(2026, 9, 20)), SCHEDULE[3])
        self.assertEqual(lessons_for(date(2026, 9, 27)), ())

    def test_exception_marker_uses_sunday_override(self):
        day = date(2026, 9, 20)
        self.assertEqual(lesson_marker(day, datetime(2026, 9, 20, 19, 15)), (10, "live"))
        self.assertEqual(lesson_marker(day, datetime(2026, 9, 20, 20, 15)), (11, "next"))

    def test_external_files_override_and_validate(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            weekly = directory / "timetable.json"
            exceptions = directory / "exceptions.json"
            rows = [{"start": "08:00", "end": "09:00", "course": "External course"}]
            weekly.write_text(json.dumps({"1": rows}), encoding="utf-8-sig")
            exceptions.write_text('{"2026-09-21": []}', encoding="utf-8")
            try:
                load_schedule(directory)
                self.assertEqual(lessons_for(date(2026, 9, 21)), ())
                self.assertEqual(lessons_for(date(2026, 9, 28))[0].course, "External course")
                for invalid in ('[]', '{', '{"2026-02-30": []}', '{"2026.09.20": []}'):
                    exceptions.write_text(invalid, encoding="utf-8")
                    with self.assertRaises(ValueError):
                        load_schedule(directory)
                exceptions.write_text('{}', encoding="utf-8")
                for invalid in ({"8": []}, {"1": rows * 2},
                                {"1": [{"start": "25:00", "end": "26:00", "course": "bad"}]}):
                    weekly.write_text(json.dumps(invalid), encoding="utf-8")
                    with self.assertRaises(ValueError):
                        load_schedule(directory)
                weekly.unlink()
                with self.assertRaises(ValueError):
                    load_schedule(directory)
            finally:
                load_schedule()

    def test_frozen_path_is_beside_executable(self):
        executable = Path(tempfile.gettempdir()) / "portable" / "Timetable.exe"
        with patch("schedule.sys.frozen", True, create=True), patch("schedule.sys.executable", str(executable)):
            self.assertEqual(data_directory(), executable.parent.resolve())

    def test_relative_layout_survives_dpi_and_resolution_change(self):
        # Work areas expressed in physical pixels, independent of scale setting.
        first = (0, 0, 2560, 1528)
        second = (0, 0, 3860, 2016)
        original = (1900, 200, 550, 900)
        state = capture_rect(original, first)
        self.assertEqual(restore_rect(state, first), original)
        restored = restore_rect(state, second)
        self.assertAlmostEqual(restored[2]/second[3], original[2]/first[3], places=3)
        self.assertAlmostEqual(restored[3]/second[3], original[3]/first[3], places=3)
        self.assertLessEqual(restored[0]+restored[2], second[2])

    def test_corrupt_settings_and_negative_monitor_origin(self):
        state = clean_state({"width": float("nan"), "height": -4, "x": "bad"})
        x, y, w, h = restore_rect(state, (-2560, -100, 2560, 1500))
        self.assertGreaterEqual(x, -2560)
        self.assertLessEqual(x+w, 0)
        self.assertGreaterEqual(y, -100)
        self.assertLessEqual(y+h, 1400)


if __name__ == "__main__":
    unittest.main()

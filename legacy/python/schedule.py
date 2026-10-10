"""Daily time ranges transcribed from 物化生2.pdf (including cell overrides)."""

import json
import re
import sys
from pathlib import Path
from dataclasses import dataclass
from datetime import date, datetime


@dataclass(frozen=True)
class Lesson:
    start: str
    end: str
    course: str

    @property
    def time_label(self):
        return f"{self.start}–{self.end}"


SCHEDULE = {}
EXCEPTIONS = {}


def data_directory():
    # Frozen programs read beside the exe, never from the extraction directory.
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


def validate_lessons(raw):
    if not isinstance(raw, dict):
        raise ValueError("Expected an object mapping days to lesson arrays")
    result = {}
    for key, rows in raw.items():
        if not isinstance(rows, list):
            raise ValueError(f"{key}: expected a lesson array")
        lessons = []
        previous = "00:00"
        for index, row in enumerate(rows, 1):
            label = f"{key}, lesson {index}"
            if not isinstance(row, dict) or not all(
                isinstance(row.get(field), str) for field in ("start", "end", "course")
            ):
                raise ValueError(f"{label}: start, end, course must be strings")
            lesson = Lesson(row["start"], row["end"], row["course"])
            if not all(re.fullmatch(r"(?:[01][0-9]|2[0-3]):[0-5][0-9]", t)
                       for t in (lesson.start, lesson.end)):
                raise ValueError(f"{label}: times must be HH:MM (00:00-23:59)")
            if not lesson.course.strip() or not previous <= lesson.start < lesson.end:
                raise ValueError(f"{label}: empty course, invalid order or overlapping times")
            lessons.append(lesson)
            previous = lesson.end
        result[key] = tuple(lessons)
    return result


def read_lessons(path):
    try:
        return validate_lessons(json.loads(path.read_text(encoding="utf-8-sig")))
    except (OSError, ValueError) as exc:
        raise ValueError(f"{path}: {exc}") from exc


def load_schedule(directory=None):
    """Validate both files before replacing data. There is no built-in fallback."""
    directory = Path(directory) if directory is not None else data_directory()
    weekly = read_lessons(directory / "timetable.json")
    if any(key not in tuple(str(day) for day in range(1, 8)) for key in weekly):
        raise ValueError(f"{directory / 'timetable.json'}: weekday keys must be 1 through 7")
    overrides = read_lessons(directory / "exceptions.json")
    for key in overrides:
        try:
            parsed = date.fromisoformat(key)
            if parsed.isoformat() != key:
                raise ValueError()
        except ValueError as exc:
            raise ValueError(f"{directory / 'exceptions.json'}: invalid YYYY-MM-DD date: {key}") from exc
    SCHEDULE.clear()
    SCHEDULE.update({int(key) - 1: rows for key, rows in weekly.items()})
    EXCEPTIONS.clear()
    EXCEPTIONS.update(overrides)


WEEKDAYS = ("星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日")


def lessons_for(day: date):
    if day.isoformat() in EXCEPTIONS:
        return EXCEPTIONS[day.isoformat()]
    return SCHEDULE.get(day.weekday(), ())


def lesson_marker(day: date, now: datetime):
    """Return (row index, live/next), using end-exclusive local time ranges."""
    if day != now.date():
        return None
    clock = now.strftime("%H:%M")
    for index, lesson in enumerate(lessons_for(day)):
        if lesson.start <= clock < lesson.end:
            return index, "live"
        if clock < lesson.start:
            return index, "next"
    return None

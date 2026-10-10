"""Isolated editor drafts and transactional saves of the external timetable."""

from copy import deepcopy
from dataclasses import asdict
from datetime import date
import json
import os
from pathlib import Path
import tempfile

from schedule import data_directory, read_lessons, validate_lessons, SCHEDULE, EXCEPTIONS


def validate_template(rows):
    if not isinstance(rows, list):
        raise ValueError("时间模板应为时间段数组")
    if any(not isinstance(row, dict) for row in rows):
        raise ValueError("每个模板时间段应包含 start 和 end")
    validate_lessons({"时间模板": [dict(row, course="时间段") for row in rows]})


def validate_draft(data):
    weekly = validate_lessons(data["weekly"])
    if any(key not in [str(day) for day in range(1, 8)] for key in weekly):
        raise ValueError("主课表的星期必须为 1 至 7")
    exceptions = validate_lessons(data["exceptions"])
    for key in exceptions:
        if date.fromisoformat(key).isoformat() != key:
            raise ValueError("例外日期必须为 YYYY-MM-DD")
    validate_template(data["template"])
    return weekly, exceptions


def _replace_bytes(path, content):
    # Stage on the same filesystem so os.replace is atomic for each file.
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".timetable-", suffix=".tmp",
                                         delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


def write_transaction(files):
    """Restore original files on an ordinary write failure; never publish a partial save."""
    original = {path: path.read_bytes() if path.exists() else None for path in files}
    replaced = []
    try:
        for path, content in files.items():
            _replace_bytes(path, content)
            replaced.append(path)
    except OSError as exc:
        failures = []
        for path in reversed(replaced):
            try:
                if original[path] is None:
                    path.unlink()
                else:
                    _replace_bytes(path, original[path])
            except OSError:
                failures.append(path.name)
        if failures:
            raise OSError("保存失败，部分文件无法还原：" + "、".join(failures)) from exc
        raise


class ScheduleStore:
    def __init__(self, directory=None):
        self.directory = Path(directory) if directory is not None else data_directory()
        weekly = read_lessons(self.directory / "timetable.json")
        exceptions = read_lessons(self.directory / "exceptions.json")
        template_path = self.directory / "template.json"
        if template_path.exists():
            try:
                template = json.loads(template_path.read_text(encoding="utf-8-sig"))
                validate_template(template)
            except (OSError, ValueError, TypeError) as exc:
                raise ValueError(f"{template_path}: {exc}") from exc
        else:
            source = weekly.get("4") or next((rows for rows in weekly.values() if rows), ())
            template = [{"start": row.start, "end": row.end} for row in source]
        self.data = {"weekly": {key: [asdict(row) for row in rows] for key, rows in weekly.items()},
                     "exceptions": {key: [asdict(row) for row in rows] for key, rows in exceptions.items()},
                     "template": template}
        validate_draft(self.data)
        self.publish()

    def snapshot(self):
        return deepcopy(self.data)

    def publish(self):
        weekly, exceptions = validate_draft(self.data)
        SCHEDULE.clear()
        SCHEDULE.update({int(key) - 1: rows for key, rows in weekly.items()})
        EXCEPTIONS.clear()
        EXCEPTIONS.update(exceptions)

    def save(self, data):
        saved = deepcopy(data)
        validate_draft(saved)
        files = {}
        for filename, key in (("timetable.json", "weekly"), ("exceptions.json", "exceptions"),
                              ("template.json", "template")):
            files[self.directory / filename] = (json.dumps(saved[key], ensure_ascii=False, indent=2)
                                                 + "\n").encode("utf-8")
        write_transaction(files)
        self.data = saved
        self.publish()


class EditorDraft:
    def __init__(self, data):
        self.data = deepcopy(data)
        for day in range(1, 6):
            self.data["weekly"].setdefault(str(day), [])
        self.mark_saved()

    def mark_saved(self):
        self.baseline = deepcopy(self.data)

    @property
    def dirty(self):
        return self.data != self.baseline

    def apply_template(self, section, keys):
        # Callers must obtain overwrite confirmation before invoking this.
        for key in keys:
            self.data[section][key] = [dict(row, course="") for row in self.data["template"]]

    def add_date(self, day, copy_day=None):
        key = day.isoformat()
        if key in self.data["exceptions"]:
            raise ValueError("这个日期已经存在")
        self.data["exceptions"][key] = deepcopy(self.data["weekly"].get(str(copy_day), [])) if copy_day else []
        return key

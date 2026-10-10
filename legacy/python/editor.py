"""Visual timetable editor with a shared time template and isolated draft state."""

from datetime import date

from PyQt5.QtCore import (Qt, QDate, QTime, QPoint, QPropertyAnimation,
                          QSequentialAnimationGroup, pyqtSignal)
from PyQt5.QtWidgets import (QApplication, QComboBox, QDateEdit, QDialog, QFrame,
                             QHBoxLayout, QLabel, QLineEdit, QMessageBox, QPushButton,
                             QScrollArea, QTabWidget, QTimeEdit, QToolButton,
                             QVBoxLayout, QWidget)

from editor_data import EditorDraft, validate_template
from schedule import WEEKDAYS
from ui_styles import EDITOR_STYLE


COURSES = ("语文", "数学", "英语", "体育", "物理", "化学", "等级组合1", "等级组合2", "等级组合3")


def label(text, name=None, wrap=False):
    widget = QLabel(text)
    if name:
        widget.setObjectName(name)
    widget.setWordWrap(wrap)
    return widget


def button(text, callback=None, name=None):
    widget = QPushButton(text)
    if name:
        widget.setObjectName(name)
    if callback:
        widget.clicked.connect(callback)
    widget.setCursor(Qt.PointingHandCursor)
    return widget


def clear_layout(layout):
    while layout.count():
        item = layout.takeAt(0)
        if item.widget():
            item.widget().hide()
            item.widget().deleteLater()
        elif item.layout():
            clear_layout(item.layout())


def confirm(parent, title, text, action="确认覆盖"):
    box = QMessageBox(QMessageBox.Warning, title, text, parent=parent)
    yes = box.addButton(action, QMessageBox.AcceptRole)
    cancel = box.addButton("取消", QMessageBox.RejectRole)
    box.setDefaultButton(cancel)
    box.exec_()
    return box.clickedButton() is yes


class TimeRangeDialog(QDialog):
    def __init__(self, start="08:00", end="08:40", rows=(), exclude=None, parent=None):
        super().__init__(parent)
        self.setWindowTitle("设置课程时间")
        self.setStyleSheet(EDITOR_STYLE)
        self.setMinimumWidth(350)
        self.rows = rows
        self.exclude = exclude
        layout = QVBoxLayout(self)
        layout.setContentsMargins(24, 22, 24, 22)
        layout.setSpacing(14)
        layout.addWidget(label("设置时间段", "columnTitle"))
        layout.addWidget(label("可以设置独立时间，无需与模板相同。", "hint", True))
        times = QHBoxLayout()
        self.start = QTimeEdit(QTime.fromString(start, "HH:mm"))
        self.end = QTimeEdit(QTime.fromString(end, "HH:mm"))
        for field in (self.start, self.end):
            field.setDisplayFormat("HH:mm")
            field.setButtonSymbols(QTimeEdit.UpDownArrows)
        times.addWidget(self.start)
        times.addWidget(label("至"))
        times.addWidget(self.end)
        layout.addLayout(times)
        self.error = label("", "hint", True)
        self.error.setStyleSheet("color: #c34b59;")
        layout.addWidget(self.error)
        actions = QHBoxLayout()
        actions.addWidget(button("取消", self.reject))
        actions.addWidget(button("确定", self.check_and_accept, "primary"))
        layout.addLayout(actions)

    @property
    def value(self):
        return {"start": self.start.time().toString("HH:mm"), "end": self.end.time().toString("HH:mm")}

    def check_and_accept(self):
        value = self.value
        if value["start"] >= value["end"]:
            self.error.setText("结束时间必须晚于开始时间。")
            return
        for row in self.rows:
            if row is not self.exclude and value["start"] < row["end"] and row["start"] < value["end"]:
                self.error.setText(f"与 {row['start']}–{row['end']} 重叠，请调整时间。")
                return
        self.accept()


class LessonCard(QFrame):
    def __init__(self, row, change_time, delete, changed, template=False):
        super().__init__()
        self.setObjectName("templateCard" if template else "lessonCard")
        self.row = row
        layout = QVBoxLayout(self)
        layout.setContentsMargins(10, 9, 10, 9)
        layout.setSpacing(6)
        top = QHBoxLayout()
        top.setSpacing(2)
        self.time_button = button(f"{row['start']}–{row['end']}", change_time, "timeButton")
        self.time_button.setToolTip("点击修改开始和结束时间")
        top.addWidget(self.time_button, 1)
        remove = QToolButton()
        remove.setObjectName("deleteCard")
        remove.setText("×")
        remove.setFixedSize(22, 22)
        remove.setCursor(Qt.PointingHandCursor)
        remove.setToolTip("删除时间段" if template else "删除这节课程")
        remove.clicked.connect(delete)
        top.addWidget(remove)
        layout.addLayout(top)
        if template:
            return
        self.course = QComboBox()
        self.course.addItem("请选择课程", "")
        for name in COURSES:
            self.course.addItem(name, name)
        self.course.addItem("自定义…", None)
        self.custom = QLineEdit()
        self.custom.setPlaceholderText("输入课程名称")
        self.custom.setMaxLength(80)
        name = row.get("course", "")
        index = self.course.findData(name)
        self.course.setCurrentIndex(index if index >= 0 else self.course.count() - 1)
        self.custom.setText(name if index < 0 else "")
        self.custom.setVisible(index < 0)
        layout.addWidget(self.course)
        layout.addWidget(self.custom)

        def choose():
            custom = self.course.currentData() is None
            self.custom.setVisible(custom)
            row["course"] = self.custom.text().strip() if custom else self.course.currentData()
            changed()
            if custom:
                self.custom.setFocus()

        def type_name(value):
            if self.course.currentData() is None:
                row["course"] = value.strip()
                changed()

        self.course.currentIndexChanged.connect(choose)
        self.custom.textChanged.connect(type_name)


class DayColumn(QFrame):
    def __init__(self, editor, section, key):
        super().__init__()
        self.editor, self.section, self.key = editor, section, key
        self.setObjectName("weekColumn" if section == "weekly" else "dayPanel")
        self.setMinimumWidth(172 if section == "weekly" else 208)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(10, 16, 10, 12)
        layout.setSpacing(12)
        if section == "weekly":
            layout.addWidget(label(WEEKDAYS[int(key) - 1], "columnTitle"))
            layout.addWidget(label("主课表", "eyebrow"))
        else:
            day = date.fromisoformat(key)
            heading = QHBoxLayout()
            heading.addWidget(label(f"{day.month}月{day.day}日", "columnTitle"), 1)
            remove = QToolButton()
            remove.setObjectName("danger")
            remove.setText("删除")
            remove.setToolTip("删除整个例外日期")
            remove.clicked.connect(lambda: editor.delete_date(key))
            heading.addWidget(remove)
            layout.addLayout(heading)
            layout.addWidget(label(f"{day.year} · {WEEKDAYS[day.weekday()]}", "eyebrow"))
        self.scroll = QScrollArea()
        self.scroll.setWidgetResizable(True)
        self.scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        body = QWidget()
        self.cards = QVBoxLayout(body)
        self.cards.setContentsMargins(0, 0, 2, 0)
        self.cards.setSpacing(8)
        self.scroll.setWidget(body)
        layout.addWidget(self.scroll, 1)
        layout.addWidget(button("＋ 添加课程", self.add_lesson))
        self.rebuild()

    @property
    def rows(self):
        return self.editor.draft.data[self.section][self.key]

    def rebuild(self):
        position = self.scroll.verticalScrollBar().value()
        clear_layout(self.cards)
        for row in self.rows:
            self.cards.addWidget(LessonCard(row, lambda checked=False, r=row: self.change_time(r),
                                            lambda checked=False, r=row: self.delete_lesson(r),
                                            self.editor.changed))
        if not self.rows:
            text = "没有课程\n点击下方添加" if self.section == "weekly" else "当天放假\n可添加课程"
            empty = label(text, "hint", True)
            empty.setAlignment(Qt.AlignCenter)
            self.cards.addWidget(empty)
        self.cards.addStretch()
        self.scroll.verticalScrollBar().setValue(position)

    def add_lesson(self):
        start, end = suggested_time(self.rows)
        dialog = TimeRangeDialog(start, end, self.rows, parent=self.editor)
        if dialog.exec_() == QDialog.Accepted:
            self.rows.append(dict(dialog.value, course=""))
            self.rows.sort(key=lambda row: row["start"])
            self.rebuild()
            self.editor.changed()

    def change_time(self, row):
        dialog = TimeRangeDialog(row["start"], row["end"], self.rows, row, self.editor)
        if dialog.exec_() == QDialog.Accepted:
            row.update(dialog.value)
            self.rows.sort(key=lambda value: value["start"])
            self.rebuild()
            self.editor.changed()

    def delete_lesson(self, row):
        self.rows.remove(row)
        self.rebuild()
        self.editor.changed()


def suggested_time(rows):
    if not rows:
        return "08:00", "08:40"
    end = QTime.fromString(rows[-1]["end"], "HH:mm")
    minutes = end.hour() * 60 + end.minute()
    if minutes + 50 <= 1439:
        return end.addSecs(600).toString("HH:mm"), end.addSecs(3000).toString("HH:mm")
    # Find the first free interval when the final lesson is near midnight.
    previous = 0
    for row in rows:
        start = QTime.fromString(row["start"], "HH:mm")
        boundary = start.hour() * 60 + start.minute()
        if boundary - previous >= 40:
            return QTime(previous // 60, previous % 60).toString("HH:mm"), QTime((previous + 40) // 60, (previous + 40) % 60).toString("HH:mm")
        finish = QTime.fromString(row["end"], "HH:mm")
        previous = finish.hour() * 60 + finish.minute()
    return "08:00", "08:40"


class TemplatePanel(QFrame):
    def __init__(self, editor, section):
        super().__init__()
        self.editor, self.section = editor, section
        self.setObjectName("templatePanel")
        self.setFixedWidth(238)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 18, 16, 16)
        layout.setSpacing(12)
        layout.addWidget(label("共享时间模板", "columnTitle"))
        layout.addWidget(label("两种课表共用。应用后再选择课程；每节课仍可单独调整时间。", "hint", True))
        self.scroll = QScrollArea()
        self.scroll.setWidgetResizable(True)
        self.scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        body = QWidget()
        self.cards = QVBoxLayout(body)
        self.cards.setContentsMargins(0, 0, 2, 0)
        self.cards.setSpacing(8)
        self.scroll.setWidget(body)
        layout.addWidget(self.scroll, 1)
        layout.addWidget(button("＋ 添加时间段", self.add_time))
        if section == "weekly":
            layout.addWidget(button("应用到所有工作日", lambda: editor.apply_template("weekly", [str(day) for day in range(1, 6)])))
        self.target = QComboBox()
        layout.addWidget(self.target)
        self.apply_button = button("应用到所选日期", self.apply_selected)
        layout.addWidget(self.apply_button)
        self.target.currentIndexChanged.connect(self.update_apply_label)
        self.refresh_targets()
        self.rebuild()

    @property
    def rows(self):
        return self.editor.draft.data["template"]

    def refresh_targets(self):
        current = self.target.currentData()
        self.target.blockSignals(True)
        self.target.clear()
        if self.section == "weekly":
            for index in range(5):
                self.target.addItem(WEEKDAYS[index], str(index + 1))
        else:
            for key in self.editor.draft.data["exceptions"]:
                day = date.fromisoformat(key)
                self.target.addItem(f"{day.month}月{day.day}日 · {day.year}", key)
        index = self.target.findData(current)
        if index >= 0:
            self.target.setCurrentIndex(index)
        self.target.blockSignals(False)
        self.update_apply_label()

    def update_apply_label(self):
        key = self.target.currentData()
        if key is None:
            self.apply_button.setText("请先添加例外日期")
            self.apply_button.setEnabled(False)
        else:
            name = WEEKDAYS[int(key) - 1] if self.section == "weekly" else f"{date.fromisoformat(key).month}月{date.fromisoformat(key).day}日"
            self.apply_button.setText(f"将模板应用到 {name}")
            self.apply_button.setEnabled(True)

    def apply_selected(self):
        key = self.target.currentData()
        if key is not None:
            self.editor.apply_template(self.section, [key])

    def rebuild(self):
        position = self.scroll.verticalScrollBar().value()
        clear_layout(self.cards)
        for row in self.rows:
            self.cards.addWidget(LessonCard(row, lambda checked=False, r=row: self.change_time(r),
                                            lambda checked=False, r=row: self.delete_time(r),
                                            self.editor.changed, template=True))
        if not self.rows:
            self.cards.addWidget(label("添加时间段，建立你的模板。", "hint", True))
        self.cards.addStretch()
        self.scroll.verticalScrollBar().setValue(position)

    def add_time(self):
        start, end = suggested_time(self.rows)
        dialog = TimeRangeDialog(start, end, self.rows, parent=self.editor)
        if dialog.exec_() == QDialog.Accepted:
            self.rows.append(dialog.value)
            self.rows.sort(key=lambda row: row["start"])
            self.editor.template_changed()

    def change_time(self, row):
        dialog = TimeRangeDialog(row["start"], row["end"], self.rows, row, self.editor)
        if dialog.exec_() == QDialog.Accepted:
            row.update(dialog.value)
            self.rows.sort(key=lambda value: value["start"])
            self.editor.template_changed()

    def delete_time(self, row):
        self.rows.remove(row)
        self.editor.template_changed()


class AddDateDialog(QDialog):
    def __init__(self, existing, parent=None):
        super().__init__(parent)
        self.existing = existing
        self.setWindowTitle("添加例外日期")
        self.setMinimumWidth(360)
        self.setStyleSheet(EDITOR_STYLE)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(24, 22, 24, 22)
        layout.setSpacing(14)
        layout.addWidget(label("添加例外日期", "columnTitle"))
        layout.addWidget(label("选择日期", "hint"))
        self.day = QDateEdit(QDate.currentDate())
        self.day.setDisplayFormat("yyyy年M月d日")
        self.day.setCalendarPopup(True)
        layout.addWidget(self.day)
        layout.addWidget(label("初始课程", "hint"))
        self.source = QComboBox()
        self.source.addItem("空白课表（当天放假）", None)
        for day in range(1, 6):
            self.source.addItem(f"复制主课表 · {WEEKDAYS[day - 1]}", day)
        layout.addWidget(self.source)
        self.error = label("", "hint")
        self.error.setStyleSheet("color: #c34b59;")
        layout.addWidget(self.error)
        actions = QHBoxLayout()
        actions.addWidget(button("取消", self.reject))
        actions.addWidget(button("添加日期", self.check_and_accept, "primary"))
        layout.addLayout(actions)

    def check_and_accept(self):
        if self.day.date().toString("yyyy-MM-dd") in self.existing:
            self.error.setText("这个日期已存在，请选择其他日期。")
            return
        self.accept()


class EditorDialog(QDialog):
    saved = pyqtSignal()
    closeCancelled = pyqtSignal()

    def __init__(self, store):
        # This is a normal, focusable window, independent of the desktop owner.
        super().__init__(None, Qt.Window | Qt.WindowCloseButtonHint | Qt.WindowMinMaxButtonsHint)
        self.store = store
        self.draft = EditorDraft(store.snapshot())
        self._close_pending = False
        self._approved_close = False
        self._shake = None
        self.setWindowTitle("编辑课表")
        self.setStyleSheet(EDITOR_STYLE)
        self.setMinimumSize(820, 550)
        screen = QApplication.primaryScreen().availableGeometry()
        self.resize(min(1260, screen.width() - 60), min(840, screen.height() - 70))
        self.template_panels = {}
        self.columns = {"weekly": {}, "exceptions": {}}
        root = QVBoxLayout(self)
        root.setContentsMargins(26, 24, 26, 20)
        root.setSpacing(18)
        heading = QHBoxLayout()
        titles = QVBoxLayout()
        titles.setSpacing(5)
        titles.addWidget(label("编辑课表", "title"))
        titles.addWidget(label("统一时间，也为每一天保留自由。", "subtitle"))
        heading.addLayout(titles, 1)
        self.save_button = button("保存课表", self.save_changes, "primary")
        self.save_button.setMinimumWidth(140)
        heading.addWidget(self.save_button)
        root.addLayout(heading)
        self.tabs = QTabWidget()
        self.tabs.setDocumentMode(True)
        root.addWidget(self.tabs, 1)
        for section, title in (("weekly", "主课表"), ("exceptions", "例外日期")):
            self.tabs.addTab(self.build_tab(section), title)
        footer = QHBoxLayout()
        self.status = label("", "status")
        footer.addWidget(self.status, 1)
        footer.addWidget(button("关闭", self.close))
        root.addLayout(footer)
        self.changed()

    def build_tab(self, section):
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(0, 18, 0, 0)
        layout.setSpacing(12)
        toolbar = QHBoxLayout()
        if section == "weekly":
            toolbar.addWidget(label("周一至周五 · 点击课程选择名称，点击时间修改时段。", "hint"), 1)
        else:
            toolbar.addWidget(label("例外日期覆盖当天主课表；保留空列表示放假。", "hint"), 1)
            toolbar.addWidget(button("＋ 添加日期", self.add_date))
        layout.addLayout(toolbar)
        body = QHBoxLayout()
        body.setSpacing(20)
        panel = TemplatePanel(self, section)
        self.template_panels[section] = panel
        body.addWidget(panel)
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setVerticalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        days = QFrame()
        days.setObjectName("daysGroup" if section == "weekly" else "exceptionsGroup")
        columns_layout = QHBoxLayout(days)
        inset = 5 if section == "weekly" else 0
        columns_layout.setContentsMargins(inset, 0, inset, 0)
        columns_layout.setSpacing(0 if section == "weekly" else 16)
        if section == "weekly":
            for day in range(1, 6):
                column = DayColumn(self, section, str(day))
                self.columns[section][str(day)] = column
                columns_layout.addWidget(column, 1)
        else:
            self.exception_layout = columns_layout
            self.exception_scroll = scroll
            self.rebuild_exceptions()
        scroll.setWidget(days)
        body.addWidget(scroll, 1)
        layout.addLayout(body, 1)
        return page

    def changed(self):
        self.status.setText("有未保存的修改 · 保存后立即更新桌面课表" if self.draft.dirty else "所有修改已保存")
        self.save_button.setText("保存课表" if self.draft.dirty else "已保存")
        self.save_button.setEnabled(self.draft.dirty)

    def template_changed(self):
        for panel in self.template_panels.values():
            panel.rebuild()
        self.changed()

    def apply_template(self, section, keys):
        if not self.draft.data["template"]:
            QMessageBox.information(self, "模板为空", "请先添加至少一个时间段。")
            return False
        try:
            validate_template(self.draft.data["template"])
        except ValueError as exc:
            QMessageBox.warning(self, "时间模板有误", str(exc))
            return False
        occupied = [key for key in keys if self.draft.data[section][key]]
        if occupied:
            names = [WEEKDAYS[int(key) - 1] if section == "weekly" else key for key in occupied]
            if not confirm(self, "覆盖当前内容？", "应用模板会清空当前内容并重新创建空课程卡片。\n\n将覆盖：" + "、".join(names)):
                return False
        self.draft.apply_template(section, keys)
        for key in keys:
            self.columns[section][key].rebuild()
        self.changed()
        return True

    def rebuild_exceptions(self):
        clear_layout(self.exception_layout)
        self.columns["exceptions"].clear()
        for key in self.draft.data["exceptions"]:
            column = DayColumn(self, "exceptions", key)
            column.setFixedWidth(224)
            self.columns["exceptions"][key] = column
            self.exception_layout.addWidget(column)
        if not self.draft.data["exceptions"]:
            empty = label("还没有例外日期\n点击「添加日期」，为调课或假期创建安排。", "hint", True)
            empty.setAlignment(Qt.AlignCenter)
            self.exception_layout.addWidget(empty, 1)
        else:
            self.exception_layout.addStretch()
        panel = self.template_panels.get("exceptions")
        if panel:
            panel.refresh_targets()

    def add_date(self):
        dialog = AddDateDialog(self.draft.data["exceptions"], self)
        if dialog.exec_() != QDialog.Accepted:
            return
        self.draft.add_date(dialog.day.date().toPyDate(), dialog.source.currentData())
        self.rebuild_exceptions()
        self.changed()
        # Scroll to the new rightmost date after layout sizes settle.
        from PyQt5.QtCore import QTimer
        QTimer.singleShot(0, lambda: self.exception_scroll.horizontalScrollBar().setValue(self.exception_scroll.horizontalScrollBar().maximum()))

    def delete_date(self, key):
        if not confirm(self, "删除例外日期？", f"删除 {key} 的全部例外课程？\n保存后，该日期将恢复使用主课表。", "删除日期"):
            return False
        del self.draft.data["exceptions"][key]
        self.rebuild_exceptions()
        self.changed()
        return True

    def save_changes(self):
        for section in ("weekly", "exceptions"):
            for key, rows in self.draft.data[section].items():
                for index, row in enumerate(rows, 1):
                    if not row.get("course", "").strip():
                        self.tabs.setCurrentIndex(0 if section == "weekly" else 1)
                        name = WEEKDAYS[int(key) - 1] if section == "weekly" else key
                        QMessageBox.warning(self, "还有课程未填写", f"{name} 的第 {index} 节还没有课程名称。\n请选择课程、输入自定义名称，或删除这张卡片。")
                        return False
        try:
            self.store.save(self.draft.data)
        except (OSError, ValueError) as exc:
            QMessageBox.critical(self, "保存失败", f"修改仍保留在编辑器中，请处理后重试。\n\n{exc}")
            return False
        self.draft.mark_saved()
        self.changed()
        self.saved.emit()
        return True

    def reject(self):
        self.close()

    def closeEvent(self, event):
        if self._approved_close or not self.draft.dirty:
            event.accept()
            self.done(QDialog.Rejected)
            return
        event.ignore()
        if self._close_pending:
            return
        self._close_pending = True
        self._shake = QSequentialAnimationGroup(self)
        origin = self.pos()
        for offset in (-9, 9, -6, 6, 0):
            animation = QPropertyAnimation(self, b"pos", self._shake)
            animation.setDuration(40)
            animation.setEndValue(origin + QPoint(offset, 0))
            self._shake.addAnimation(animation)
        self._shake.finished.connect(self.ask_to_close)
        self._shake.start()

    def ask_to_close(self):
        if self._shake:
            self._shake.deleteLater()
            self._shake = None
        box = QMessageBox(QMessageBox.Warning, "课表还没有保存", "保存修改，还是放弃本次编辑？", parent=self)
        save = box.addButton("保存并关闭", QMessageBox.AcceptRole)
        discard = box.addButton("放弃修改", QMessageBox.DestructiveRole)
        keep = box.addButton("继续编辑", QMessageBox.RejectRole)
        box.setDefaultButton(keep)
        box.exec_()
        self._close_pending = False
        selected = box.clickedButton()
        if selected is discard or (selected is save and self.save_changes()):
            self._approved_close = True
            self.close()
        else:
            self.closeCancelled.emit()

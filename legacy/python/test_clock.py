"""A monotonic simulated clock and a dial for testing timetable boundaries."""

from datetime import datetime, timedelta
import math
import time

from PyQt5.QtCore import Qt, QDate, QTime, QPointF, QRectF, QTimer, pyqtSignal
from PyQt5.QtGui import QColor, QFont, QPainter, QPen
from PyQt5.QtWidgets import QDateEdit, QDialog, QHBoxLayout, QLabel, QPushButton, QTimeEdit, QVBoxLayout, QWidget

from ui_styles import EDITOR_STYLE


class SimulationClock:
    def __init__(self):
        self.base = None
        self.started = None

    @property
    def active(self):
        return self.base is not None

    def now(self):
        if not self.active:
            return datetime.now()
        return self.base + timedelta(seconds=time.monotonic() - self.started)

    def set(self, value):
        self.base = value
        self.started = time.monotonic()

    def reset(self):
        self.base = self.started = None


class ClockDial(QWidget):
    timeChanged = pyqtSignal(QTime)

    def __init__(self):
        super().__init__()
        self.setMinimumSize(260, 260)
        self.value = QTime.currentTime()
        self.hand = None
        self.previous_angle = None
        self.remainder = 0.0
        self.setCursor(Qt.OpenHandCursor)

    def set_time(self, value):
        self.value = value
        self.update()

    def hand_angle(self, hand):
        if hand == "minute":
            return self.value.minute() * 6
        return (self.value.hour() % 12) * 30 + self.value.minute() * .5

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        center = QPointF(self.width() / 2, self.height() / 2)
        radius = min(self.width(), self.height()) / 2 - 12
        painter.setPen(QPen(QColor("#e1e6ef"), 1))
        painter.setBrush(QColor("#ffffff"))
        painter.drawEllipse(center, radius, radius)
        painter.translate(center)
        font = QFont("Segoe UI", 12)
        painter.setFont(font)
        for index in range(60):
            angle = math.radians(index * 6 - 90)
            length = 9 if index % 5 == 0 else 4
            painter.setPen(QPen(QColor("#657792" if index % 5 == 0 else "#d1d9e6"), 2 if index % 5 == 0 else 1))
            painter.drawLine(QPointF(math.cos(angle) * (radius - 8), math.sin(angle) * (radius - 8)),
                             QPointF(math.cos(angle) * (radius - 8 - length), math.sin(angle) * (radius - 8 - length)))
            if index % 5 == 0:
                position = QPointF(math.cos(angle) * (radius - 30), math.sin(angle) * (radius - 30))
                painter.setPen(QColor("#52647e"))
                painter.drawText(QRectF(position.x() - 15, position.y() - 12, 30, 24), Qt.AlignCenter,
                                 str(index // 5 or 12))
        for hand, fraction, color, width in (("hour", .48, "#243047", 6), ("minute", .73, "#3267e3", 4)):
            angle = math.radians(self.hand_angle(hand) - 90)
            painter.setPen(QPen(QColor(color), width, Qt.SolidLine, Qt.RoundCap))
            painter.drawLine(QPointF(0, 0), QPointF(math.cos(angle) * radius * fraction,
                                                 math.sin(angle) * radius * fraction))
        painter.setPen(Qt.NoPen)
        painter.setBrush(QColor("#3267e3"))
        painter.drawEllipse(QPointF(0, 0), 6, 6)

    def angle(self, point):
        return (math.degrees(math.atan2(point.y() - self.height() / 2,
                                        point.x() - self.width() / 2)) + 90) % 360

    def mousePressEvent(self, event):
        if event.button() != Qt.LeftButton:
            return
        position = event.localPos() - QPointF(self.width() / 2, self.height() / 2)
        radius = min(self.width(), self.height()) / 2 - 12

        def distance(hand, fraction):
            angle = math.radians(self.hand_angle(hand) - 90)
            tip = QPointF(math.cos(angle) * radius * fraction, math.sin(angle) * radius * fraction)
            projection = max(0, min(1, (position.x() * tip.x() + position.y() * tip.y()) /
                                    max(1, tip.x() ** 2 + tip.y() ** 2)))
            delta = position - tip * projection
            return math.hypot(delta.x(), delta.y())

        self.hand = "hour" if distance("hour", .48) < distance("minute", .73) else "minute"
        self.previous_angle = self.angle(event.localPos())
        self.remainder = 0.0
        self.setCursor(Qt.ClosedHandCursor)

    def mouseMoveEvent(self, event):
        if self.hand is None:
            return
        angle = self.angle(event.localPos())
        delta = (angle - self.previous_angle + 180) % 360 - 180
        self.previous_angle = angle
        self.remainder += delta / (6 if self.hand == "minute" else .5)
        minutes = round(self.remainder)
        if minutes:
            self.remainder -= minutes
            self.value = self.value.addSecs(minutes * 60)
            self.timeChanged.emit(self.value)
            self.update()

    def mouseReleaseEvent(self, event):
        self.hand = self.previous_angle = None
        self.setCursor(Qt.OpenHandCursor)


class TimeTestDialog(QDialog):
    changed = pyqtSignal()

    def __init__(self, clock):
        super().__init__(None, Qt.Window | Qt.WindowCloseButtonHint)
        self.clock = clock
        self.editing = False
        self.setWindowTitle("调整测试时间")
        self.setStyleSheet(EDITOR_STYLE)
        self.setMinimumWidth(390)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(26, 24, 26, 24)
        layout.setSpacing(16)
        title = QLabel("调整测试时间")
        title.setObjectName("title")
        layout.addWidget(title)
        self.status = QLabel()
        self.status.setObjectName("hint")
        layout.addWidget(self.status)
        self.dial = ClockDial()
        layout.addWidget(self.dial)
        hint = QLabel("拖动时针或分针，也可直接输入日期和时间。")
        hint.setWordWrap(True)
        hint.setObjectName("hint")
        layout.addWidget(hint)
        fields = QHBoxLayout()
        self.day = QDateEdit()
        self.day.setDisplayFormat("yyyy-MM-dd")
        self.day.setCalendarPopup(True)
        self.time = QTimeEdit()
        self.time.setDisplayFormat("HH:mm:ss")
        fields.addWidget(self.day, 1)
        fields.addWidget(self.time, 1)
        layout.addLayout(fields)
        self.day.dateChanged.connect(self.field_changed)
        self.time.timeChanged.connect(self.field_changed)
        self.dial.timeChanged.connect(self.dial_changed)
        self.apply = QPushButton("启用测试时间")
        self.apply.setObjectName("primary")
        self.apply.clicked.connect(self.apply_time)
        layout.addWidget(self.apply)
        self.reset = QPushButton("恢复电脑时间")
        self.reset.clicked.connect(self.reset_time)
        layout.addWidget(self.reset)
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.refresh)
        self.timer.start(250)
        self.refresh()

    def field_changed(self):
        self.editing = True
        self.dial.set_time(self.time.time())

    def dial_changed(self, value):
        self.time.setTime(value)
        self.editing = True

    def refresh(self):
        self.status.setText("正在使用测试时间 · 按秒继续走" if self.clock.active else "正在同步电脑时间")
        if not self.editing and not self.day.hasFocus() and not self.time.hasFocus() and self.dial.hand is None:
            now = self.clock.now()
            self.day.blockSignals(True)
            self.time.blockSignals(True)
            self.day.setDate(QDate(now.year, now.month, now.day))
            self.time.setTime(QTime(now.hour, now.minute, now.second))
            self.day.blockSignals(False)
            self.time.blockSignals(False)
            self.dial.set_time(self.time.time())

    def apply_time(self):
        self.clock.set(datetime.combine(self.day.date().toPyDate(), self.time.time().toPyTime()))
        self.editing = False
        self.changed.emit()
        self.refresh()

    def reset_time(self):
        self.clock.reset()
        self.editing = False
        self.changed.emit()
        self.refresh()

"""A transparent, DPI-aware desktop timetable. Run with python app.py."""

import argparse
import json
import logging
import os
import signal
import time
from pathlib import Path
import sys
from datetime import date, datetime, timedelta

from desktop_windows import DesktopBridge, enable_dpi_awareness
from placement import DEFAULT_STATE, capture_rect, clamp_rect, clean_state, restore_rect
from schedule import WEEKDAYS, lesson_marker, lessons_for, load_schedule, data_directory

# Set before importing Qt / creating QApplication. No bitmap scaling and no
# forced QT_SCALE_FACTOR: Qt renders text to the monitor's native backing store.
os.environ.setdefault("QT_AUTO_SCREEN_SCALE_FACTOR", "1")
os.environ.setdefault("QT_SCALE_FACTOR_ROUNDING_POLICY", "PassThrough")
enable_dpi_awareness()

from PyQt5.QtCore import QPointF, QRectF, Qt, QTimer
from PyQt5.QtGui import QColor, QFont, QFontMetricsF, QIcon, QPainter, QPainterPath, QPen, QPixmap
from PyQt5.QtNetwork import QLocalServer, QLocalSocket
from PyQt5.QtWidgets import QApplication, QMenu, QMessageBox, QSystemTrayIcon, QWidget

from winui_bridge import NativeEditor
from test_clock import SimulationClock, TimeTestDialog


def settings_directory():
    # Portable: layout and logs travel with the program, without AppData access.
    return data_directory() / "user-data"


def read_state(path):
    try:
        return clean_state(json.loads(path.read_text(encoding="utf-8")))
    except (OSError, ValueError):
        return DEFAULT_STATE.copy()


class TimetableWidget(QWidget):
    def __init__(self, settings_path=None, desktop=True, preview_day=None):
        load_schedule()
        flags = Qt.FramelessWindowHint | Qt.Tool | Qt.WindowDoesNotAcceptFocus
        if not desktop:
            # Diagnostic mode is a regular, Alt+Tab-visible application window.
            flags = Qt.FramelessWindowHint | Qt.Window
        else:
            flags |= Qt.WindowStaysOnBottomHint
        super().__init__(None, flags)
        self.setWindowTitle("桌面课表")
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setAttribute(Qt.WA_ShowWithoutActivating)
        self.setMouseTracking(True)
        self.settings_path = settings_path
        self.state = read_state(settings_path) if settings_path else DEFAULT_STATE.copy()
        self.locked = self.state["locked"]
        self.preview_day = preview_day
        self.current_date = date.today()
        self.clock = SimulationClock()
        self.editor_window = None
        self.time_test_window = None
        self._exit_requested = False
        self.marker = None
        self.bridge = DesktopBridge(self, desktop)
        self.gesture = None
        self.pending_rect = None
        self.drag_timer = QTimer(self)
        self.drag_timer.setSingleShot(True)
        self.drag_timer.setTimerType(Qt.PreciseTimer)
        self.drag_timer.timeout.connect(self.flush_drag)
        self.tray = None
        self._screen_connections = set()
        self._tray_source_menu = None
        self.refresh_timer = QTimer(self)
        self.refresh_timer.timeout.connect(self.refresh)
        self.refresh_timer.start(1000)
        self.geometry_timer = QTimer(self)
        self.geometry_timer.setSingleShot(True)
        self.geometry_timer.timeout.connect(self.restore_placement)
        self.resize(380, 632)
        self.update_marker()

    @property
    def shown_date(self):
        if self.preview_day is None:
            return self.current_date
        return self.current_date + timedelta(days=self.preview_day - self.current_date.weekday())

    @property
    def rows(self):
        return lessons_for(self.shown_date)

    @property
    def unit(self):
        return min(self.width() / 380, self.height() / 632)

    def lock_rect(self):
        u = self.unit
        return QRectF(self.width() - 54*u, 15*u, 36*u, 36*u)

    def edit_rect(self):
        u = self.unit
        return QRectF(self.width() - 96*u, 15*u, 36*u, 36*u)

    def open_editor(self):
        if self.editor_window is None:
            self.editor_window = NativeEditor(data_directory(), self)
            self.editor_window.saved.connect(self.editor_saved)
            self.editor_window.finished.connect(self.editor_closed)
            self.editor_window.closeCancelled.connect(self.cancel_exit)
            self.editor_window.failed.connect(lambda message: QMessageBox.critical(None, "编辑器", message))
            self.editor_window.start()
        else:
            self.editor_window.activate()

    def editor_closed(self, *_):
        if self.editor_window is not None:
            self.editor_window.deleteLater()
            self.editor_window = None
        if self._exit_requested:
            QTimer.singleShot(0, self.close)

    def cancel_exit(self):
        self._exit_requested = False

    def editor_saved(self):
        try:
            load_schedule()
        except ValueError as exc:
            QMessageBox.critical(None, "课表文件错误", str(exc))
            return
        self.update_marker()
        self.update()

    def open_time_test(self):
        if self.time_test_window is None:
            self.time_test_window = TimeTestDialog(self.clock)
            self.time_test_window.changed.connect(self.time_changed)
        self.time_test_window.show()
        self.time_test_window.raise_()
        self.time_test_window.activateWindow()

    def time_changed(self):
        self.preview_day = None
        self.refresh()
        self.update()

    def start(self):
        self.show()
        self.bridge.attach()
        self.restore_placement()
        self.setup_tray()
        app = QApplication.instance()
        app.screenAdded.connect(self.screens_changed)
        app.screenRemoved.connect(self.screens_changed)
        self.connect_screens()

    def connect_screens(self):
        for screen in QApplication.screens():
            if screen not in self._screen_connections:
                screen.availableGeometryChanged.connect(self.schedule_restore)
                screen.logicalDotsPerInchChanged.connect(self.schedule_restore)
                screen.geometryChanged.connect(self.schedule_restore)
                self._screen_connections.add(screen)

    def screens_changed(self, *_):
        self.connect_screens()
        self.schedule_restore()

    def schedule_restore(self, *_):
        # Let Qt finish processing WM_DPICHANGED before restoring native geometry.
        self.geometry_timer.start(250)

    def restore_placement(self):
        if self.gesture:
            self.geometry_timer.start(250)
            return
        work, name = self.bridge.initial_work_area(self.state.get("screen"))
        self.bridge.set_rect(restore_rect(self.state, work))
        self.state["screen"] = name
        self.update()

    def save_placement(self):
        rect = self.bridge.rect()
        work, name = self.bridge.work_area((rect[0]+rect[2]//2, rect[1]+rect[3]//2))
        self.state.update(capture_rect(rect, work))
        self.state.update(locked=self.locked, screen=name)
        if self.settings_path:
            try:
                self.settings_path.parent.mkdir(parents=True, exist_ok=True)
                pending = self.settings_path.with_suffix(".tmp")
                pending.write_text(json.dumps(self.state, ensure_ascii=False, indent=2), encoding="utf-8")
                pending.replace(self.settings_path)
            except OSError:
                logging.exception("Unable to save placement")

    def refresh(self):
        today = self.clock.now().date() if self.clock.active else date.today()
        if today != self.current_date:
            self.current_date = today
            self.update()
        self.update_marker()
        if self.bridge.enabled and not self.bridge.attached():
            if self.bridge.attach():
                self.restore_placement()

    def update_marker(self, now=None):
        marker = lesson_marker(self.shown_date, now or self.clock.now())
        if marker != self.marker:
            self.marker = marker
            self.update()

    def paintEvent(self, event):
        # QPainter draws directly on the high-DPI QWidget backing store. Pixel
        # font sizes below are logical pixels; Qt supplies the device pixel ratio.
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        p.setRenderHint(QPainter.TextAntialiasing)
        p.setCompositionMode(QPainter.CompositionMode_Source)
        p.fillRect(self.rect(), Qt.transparent)
        p.setCompositionMode(QPainter.CompositionMode_SourceOver)
        u = self.unit
        p.setPen(Qt.NoPen)
        # Keep white wallpapers from washing out the panel and row accents.
        p.setBrush(QColor(21, 21, 30, 242))
        p.drawRoundedRect(QRectF(self.rect()).adjusted(.5, .5, -.5, -.5), 16*u, 16*u)
        pad = 24*u

        def text(rect, value, size=16, alpha=245, bold=False, family="Microsoft YaHei UI"):
            font = QFont(family)
            font.setPixelSize(max(1, round(size*u)))
            font.setBold(bold)
            metrics = QFontMetricsF(font)
            fit = min(1.0, rect.width() / max(1, metrics.horizontalAdvance(value)),
                      rect.height() / max(1, metrics.height()))
            if fit < 1:
                font.setPixelSize(max(1, int(font.pixelSize()*fit)))
            p.setFont(font)
            p.setPen(QColor(246, 244, 240, alpha))
            p.drawText(rect, Qt.AlignLeft | Qt.AlignVCenter, value)

        d = self.shown_date
        heading = f"{WEEKDAYS[d.weekday()]}  {d:%m/%d}"
        text(QRectF(pad, 17*u, self.width()-pad-100*u, 32*u), heading, 24, bold=True)
        self.draw_lock(p, self.lock_rect(), self.locked)
        self.draw_edit(p, self.edit_rect())
        badges = []
        if self.clock.active:
            badges.append("测试时间")
        if self.preview_day is not None:
            badges.append("课表预览")
        if badges:
            text(QRectF(pad, 49*u, 180*u, 18*u), " · ".join(badges), 10, 200)
        if self.marker is not None:
            label = "正在上课" if self.marker[1] == "live" else "即将开始"
            status_x = self.width()-pad-82*u
            p.setPen(Qt.NoPen)
            p.setBrush(QColor(224, 4, 0, 235) if self.marker[1] == "live"
                       else QColor(68, 94, 132, 230))
            p.drawRoundedRect(QRectF(status_x-10*u, 51*u, 92*u, 20*u), 4*u, 4*u)
            text(QRectF(status_x, 51*u, 82*u, 20*u), label, 13, 255, bold=True)
        header_y = 72*u
        # Two columns only; allow independent window width / height resizing.
        course_x = pad + (self.width() - 2*pad) * .55
        text(QRectF(pad, header_y, course_x-pad, 26*u), "时间", 16, 220, bold=True)
        text(QRectF(course_x, header_y, self.width()-course_x-pad, 26*u), "当日课程", 16, 220, bold=True)
        line_y = 104*u
        p.setPen(QPen(QColor(255, 255, 255, 48), max(.65, u*.7)))
        p.drawLine(QPointF(pad, line_y), QPointF(self.width()-pad, line_y))
        rows = self.rows
        if rows:
            row_height = (self.height() - 128*u) / len(rows)
            for index, lesson in enumerate(rows):
                y = line_y + index * row_height
                active = self.marker is not None and self.marker[0] == index
                if active:
                    live = self.marker[1] == "live"
                    p.setPen(QPen(QColor(245, 48, 40, 180) if live else QColor(144, 176, 221, 110),
                                  max(.8, u)))
                    p.setBrush(QColor(158, 22, 29, 155) if live else QColor(68, 94, 132, 115))
                    rect = QRectF(12*u, y+2*u, self.width()-24*u, row_height-4*u)
                    shape = QPainterPath()
                    shape.addRoundedRect(rect, 5*u, 5*u)
                    p.drawPath(shape)
                    p.save()
                    p.setClipPath(shape)
                    p.fillRect(QRectF(rect.left(), rect.top(), 7*u, rect.height()),
                               QColor(255, 42, 32) if live else QColor(155, 186, 230))
                    p.restore()
                text(QRectF(pad, y, course_x-pad-10*u, row_height), lesson.time_label, 21,
                     alpha=255, bold=True, family="Segoe UI")
                text(QRectF(course_x, y, self.width()-course_x-pad, row_height), lesson.course, 24,
                     alpha=255, bold=True)
                if index < len(rows)-1:
                    p.setPen(QPen(QColor(255, 255, 255, 20), max(.5, .6*u)))
                    p.drawLine(QPointF(pad, y+row_height), QPointF(self.width()-pad, y+row_height))
        else:
            text(QRectF(pad, line_y+30*u, self.width()-pad*2, 44*u), "今天没有课程", 24, 245, bold=True)
        if not self.locked:
            p.setPen(QPen(QColor(255, 255, 255, 105), max(1, u)))
            for offset in (0, 5, 10):
                p.drawLine(QPointF(self.width()-(22-offset)*u, self.height()-9*u),
                           QPointF(self.width()-9*u, self.height()-(22-offset)*u))

    @staticmethod
    def draw_lock(p, rect, locked):
        p.save()
        p.translate(rect.x(), rect.y())
        p.scale(rect.width()/36, rect.height()/36)
        p.setPen(Qt.NoPen)
        p.setBrush(QColor(255, 255, 255))
        p.drawRoundedRect(QRectF(0, 0, 36, 36), 7, 7)
        p.setPen(QPen(QColor(36, 45, 61), 1.7, Qt.SolidLine, Qt.RoundCap, Qt.RoundJoin))
        p.setBrush(Qt.NoBrush)
        p.drawRoundedRect(QRectF(10, 16, 16, 13), 2, 2)
        path = QPainterPath()
        path.moveTo(13, 16)
        path.lineTo(13, 11)
        path.cubicTo(13, 4, 23, 4, 23, 11)
        path.lineTo(23, 16 if locked else 12)
        p.drawPath(path)
        p.drawLine(QPointF(18, 21), QPointF(18, 24))
        p.restore()

    @staticmethod
    def draw_edit(p, rect):
        p.save()
        p.translate(rect.x(), rect.y())
        p.scale(rect.width()/36, rect.height()/36)
        p.setPen(Qt.NoPen)
        p.setBrush(Qt.white)
        p.drawRoundedRect(QRectF(0, 0, 36, 36), 7, 7)
        p.setPen(QPen(QColor(36, 45, 61), 1.8, Qt.SolidLine, Qt.RoundCap, Qt.RoundJoin))
        p.setBrush(Qt.NoBrush)
        p.drawLine(QPointF(11, 26), QPointF(26, 26))
        path = QPainterPath()
        path.moveTo(11, 22)
        path.lineTo(12, 17)
        path.lineTo(22, 7)
        path.lineTo(27, 12)
        path.lineTo(17, 22)
        path.closeSubpath()
        p.drawPath(path)
        p.drawLine(QPointF(20, 9), QPointF(25, 14))
        p.restore()

    def edge_at(self, point):
        border = max(7, 9*self.unit)
        edge = ""
        if point.y() < border:
            edge += "t"
        elif point.y() > self.height()-border:
            edge += "b"
        if point.x() < border:
            edge += "l"
        elif point.x() > self.width()-border:
            edge += "r"
        return edge

    def toggle_lock(self):
        self.flush_drag()
        self.gesture = None
        self.locked = not self.locked
        self.unsetCursor()
        self.save_placement()
        self.update()

    def mousePressEvent(self, event):
        if event.button() != Qt.LeftButton:
            return
        if self.edit_rect().contains(event.localPos()):
            self.open_editor()
            return
        if self.lock_rect().contains(event.localPos()):
            self.toggle_lock()
            return
        if self.locked:
            return
        self.gesture = (self.edge_at(event.pos()), self.bridge.cursor(), self.bridge.rect())

    def mouseMoveEvent(self, event):
        on_button = self.lock_rect().contains(event.localPos()) or self.edit_rect().contains(event.localPos())
        self.setToolTip("编辑课表" if self.edit_rect().contains(event.localPos()) else
                        ("解锁位置和大小" if self.locked else "锁定位置和大小") if self.lock_rect().contains(event.localPos()) else "")
        if self.locked:
            self.setCursor(Qt.PointingHandCursor if on_button else Qt.ArrowCursor)
            return
        if not self.gesture:
            edge = self.edge_at(event.pos())
            cursor = {"tl": Qt.SizeFDiagCursor, "br": Qt.SizeFDiagCursor,
                      "tr": Qt.SizeBDiagCursor, "bl": Qt.SizeBDiagCursor,
                      "l": Qt.SizeHorCursor, "r": Qt.SizeHorCursor,
                      "t": Qt.SizeVerCursor, "b": Qt.SizeVerCursor}.get(edge, Qt.SizeAllCursor)
            self.setCursor(Qt.PointingHandCursor if on_button else cursor)
            return
        edge, origin, rect = self.gesture
        cursor = self.bridge.cursor()
        dx, dy = cursor[0]-origin[0], cursor[1]-origin[1]
        x, y, w, h = rect
        work, _ = self.bridge.work_area(cursor)
        if not edge:
            # Recompute physical dimensions on crossing monitors to preserve the
            # screen-height ratio, regardless of each monitor's Windows scale.
            previous_work, _ = self.bridge.work_area((x+w//2, y+h//2))
            ratio = work[3] / previous_work[3]
            w, h = round(w*ratio), round(h*ratio)
            x, y = cursor[0]-(origin[0]-x)*ratio, cursor[1]-(origin[1]-y)*ratio
        else:
            min_w, min_h = round(work[3]*.20), round(work[3]*.30)
            if "l" in edge:
                change = min(dx, w-min_w)
                x, w = x+change, w-change
            if "r" in edge:
                w = max(min_w, w+dx)
            if "t" in edge:
                change = min(dy, h-min_h)
                y, h = y+change, h-change
            if "b" in edge:
                h = max(min_h, h+dy)
        self.pending_rect = clamp_rect((x,y,w,h), work)
        # Coalesce high-frequency mouse events; paint a fresh native-DPI frame
        # at most once per 16 ms instead of reallocating on every input packet.
        if not self.drag_timer.isActive():
            self.drag_timer.start(16)

    def flush_drag(self):
        self.drag_timer.stop()
        if self.pending_rect is not None:
            rect, self.pending_rect = self.pending_rect, None
            self.bridge.set_rect(rect)

    def mouseReleaseEvent(self, event):
        if self.gesture:
            self.flush_drag()
            self.gesture = None
            self.save_placement()

    def contextMenuEvent(self, event):
        menu = self.make_menu()
        menu.exec_(event.globalPos())
        menu.deleteLater()

    def set_preview(self, day):
        self.preview_day = day
        self.update_marker()
        self.update()

    def reset_placement(self):
        if self.locked:
            return
        self.state = DEFAULT_STATE.copy()
        self.restore_placement()
        self.save_placement()

    def make_menu(self):
        menu = QMenu(self)
        menu.addAction("编辑课表", self.open_editor)
        menu.addAction("调整测试时间", self.open_time_test)
        menu.addSeparator()
        action = menu.addAction("锁定位置和大小")
        action.setCheckable(True)
        action.setChecked(self.locked)
        action.triggered.connect(self.toggle_lock)
        preview = menu.addMenu("查看课表")
        action = preview.addAction("自动显示今天")
        action.setCheckable(True)
        action.setChecked(self.preview_day is None)
        action.triggered.connect(lambda: self.set_preview(None))
        for day, name in enumerate(WEEKDAYS):
            action = preview.addAction(name)
            action.setCheckable(True)
            action.setChecked(self.preview_day == day)
            action.triggered.connect(lambda checked=False, value=day: self.set_preview(value))
        reset = menu.addAction("恢复默认位置和大小")
        reset.setEnabled(not self.locked)
        reset.triggered.connect(self.reset_placement)
        if self.bridge.error:
            action = menu.addAction("桌面连接状态")
            action.triggered.connect(lambda: QMessageBox.information(self, "桌面连接状态", self.bridge.error))
        menu.addSeparator()
        menu.addAction("退出", self.close)
        return menu

    def setup_tray(self):
        if not QSystemTrayIcon.isSystemTrayAvailable():
            return
        icon = QIcon()
        for size in (16, 24, 32, 48, 64):
            pix = QPixmap(size, size)
            pix.fill(Qt.transparent)
            p = QPainter(pix)
            p.setRenderHint(QPainter.Antialiasing)
            p.scale(size/32, size/32)
            p.setPen(QPen(Qt.white, 2))
            p.setBrush(QColor(20, 23, 28))
            p.drawRoundedRect(QRectF(4, 5, 24, 24), 4, 4)
            p.drawLine(4, 12, 28, 12)
            p.drawLine(10, 2, 10, 8)
            p.drawLine(22, 2, 22, 8)
            p.drawLine(10, 19, 22, 19)
            p.drawLine(10, 24, 18, 24)
            p.end()
            icon.addPixmap(pix)
        self.tray = QSystemTrayIcon(icon, self)
        self.tray.setToolTip("桌面课表 · 右键设置")
        menu = self.make_menu()
        # Rebuild on open so lock / preview states stay in sync.
        menu.aboutToShow.connect(self.update_tray_menu)
        self.tray.setContextMenu(menu)
        self.tray.show()

    def update_tray_menu(self):
        menu = self.tray.contextMenu()
        fresh = self.make_menu()
        menu.clear()
        for action in fresh.actions():
            action.setParent(menu)
            menu.addAction(action)
        # Keep submenus alive along with the menu that owns their actions.
        if self._tray_source_menu is not None:
            self._tray_source_menu.deleteLater()
        self._tray_source_menu = fresh

    def closeEvent(self, event):
        if self.editor_window is not None and self.editor_window.running:
            self._exit_requested = True
            event.ignore()
            editor = self.editor_window
            editor.close()
            return
        if self.time_test_window is not None:
            self.time_test_window.close()
        self.flush_drag()
        self.save_placement()
        if self.tray:
            self.tray.hide()
        event.accept()
        QApplication.instance().quit()


def make_application(argv):
    QApplication.setAttribute(Qt.AA_EnableHighDpiScaling)
    QApplication.setAttribute(Qt.AA_UseHighDpiPixmaps)
    if hasattr(QApplication, "setHighDpiScaleFactorRoundingPolicy"):
        QApplication.setHighDpiScaleFactorRoundingPolicy(Qt.HighDpiScaleFactorRoundingPolicy.PassThrough)
    app = QApplication(argv)
    app.setStyle("Fusion")
    app.setApplicationName("DailyDesktopTimetable")
    app.setQuitOnLastWindowClosed(False)
    return app


def main():
    started_at = time.perf_counter()
    parser = argparse.ArgumentParser(description="桌面课表")
    parser.add_argument("--preview-day", type=int, choices=range(1, 8), help="预览星期几（1–7）")
    parser.add_argument("--windowed", action="store_true", help="调试：普通窗口，不连接桌面")
    parser.add_argument("--no-save", action="store_true", help="测试时不读写位置设置")
    parser.add_argument("--quit-after", type=int, help="测试：指定毫秒后退出")
    parser.add_argument("--diagnostics", type=Path, help="测试：写出窗口和 DPI 状态")
    args = parser.parse_args()
    directory = settings_directory()
    directory.mkdir(parents=True, exist_ok=True)
    logging.basicConfig(filename=directory / "app.log", level=logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s", encoding="utf-8")
    app = make_application(sys.argv[:1])
    server = QLocalServer()
    if not args.no_save:
        probe = QLocalSocket()
        probe.connectToServer("DailyDesktopTimetable.Leon.v1")
        if probe.waitForConnected(300):
            probe.disconnectFromServer()
            if sys.stdout:
                print("课表已经在运行，请按 Win+D 查看桌面，或查看系统托盘图标。", flush=True)
            return 0
        QLocalServer.removeServer("DailyDesktopTimetable.Leon.v1")
        if not server.listen("DailyDesktopTimetable.Leon.v1"):
            return 1
        def accept_connection():
            connection = server.nextPendingConnection()
            if connection:
                connection.disconnectFromServer()
                connection.deleteLater()
        server.newConnection.connect(accept_connection)
    try:
        widget = TimetableWidget(None if args.no_save else directory / "settings.json",
                                 not args.windowed,
                                 args.preview_day - 1 if args.preview_day else None)
    except ValueError as exc:
        logging.exception("Unable to load timetable")
        QMessageBox.critical(None, "课表文件错误", str(exc))
        return 1
    widget.start()
    # Python handles SIGINT when control returns from Qt to Python (the refresh
    # timer does that periodically). Close normally instead of raising in a slot.
    signal.signal(signal.SIGINT, lambda *_: widget.close())
    if sys.stdout:
        if widget.bridge.enabled:
            status = ("已连接桌面，按 Win+D 查看；不显示在 Alt+Tab 中。"
                      if widget.bridge.attached() else f"桌面连接失败：{widget.bridge.error}")
        else:
            status = "普通窗口调试模式，可用 Alt+Tab 切换。"
        print(f"课表已启动（初始化 {time.perf_counter()-started_at:.2f} 秒）。{status}", flush=True)
        print("程序不会自动安装或下载依赖。退出可用托盘右键菜单或 Ctrl+C。", flush=True)
    if args.diagnostics:
        def write_diagnostics():
            payload = {"attached_to_desktop": widget.bridge.attached(),
                       "desktop_error": widget.bridge.error, "native_rect": widget.bridge.rect(),
                       "logical_size": [widget.width(), widget.height()],
                       "device_pixel_ratio": widget.devicePixelRatioF(),
                       "date": str(widget.shown_date), "rows": len(widget.rows)}
            args.diagnostics.write_text(json.dumps(payload, indent=2), encoding="utf-8")
        QTimer.singleShot(1500, write_diagnostics)
    if args.quit_after:
        QTimer.singleShot(args.quit_after, widget.close)
    return app.exec_()


if __name__ == "__main__":
    sys.exit(main())

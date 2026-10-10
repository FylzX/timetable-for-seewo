"""Launch the native editor and exchange events without reading account state."""

import ctypes
import json
from pathlib import Path
import sys

from PyQt5.QtCore import QObject, QProcess, pyqtSignal


def editor_executable(directory):
    directory = Path(directory)
    # The first path is for a future portable distribution; others are dev builds.
    candidates = [directory / "editor" / "Timetable.Editor.exe"]
    project = directory / "winui-editor"
    repository_project = directory.parent.parent / "winui-editor"
    if not project.is_dir() and repository_project.is_dir():
        project = repository_project
    for config in ("Debug", "Release"):
        for platform in ("x64", ""):
            candidates.append(project / "bin" / platform / config /
                              "net8.0-windows10.0.19041.0" / "win-x64" / "Timetable.Editor.exe")
    return next((path for path in candidates if path.is_file()), None)


class NativeEditor(QObject):
    saved = pyqtSignal()
    finished = pyqtSignal()
    closeCancelled = pyqtSignal()
    failed = pyqtSignal(str)

    def __init__(self, directory, parent=None):
        super().__init__(parent)
        self.directory = Path(directory)
        self.dirty = False
        self.process = QProcess(self)
        self.process.setWorkingDirectory(str(self.directory))
        self.process.readyReadStandardOutput.connect(self.read_messages)
        self.process.finished.connect(self.process_finished)
        self.process.errorOccurred.connect(self.process_error)
        self.buffer = b""

    @property
    def running(self):
        return self.process.state() != QProcess.NotRunning

    def start(self):
        executable = editor_executable(self.directory)
        if executable is None:
            self.failed.emit("WinUI 编辑器尚未进行开发构建。\n请先在项目目录手动执行：\n"
                             "dotnet build winui-editor/Timetable.Editor.csproj\n"
                             "或运行 winui-editor/run-editor.ps1 查看编辑器。")
            self.finished.emit()
            return
        self.process.start(str(executable), ["--data-dir", str(self.directory), "--bridge"])

    def send(self, message):
        if self.running:
            self.process.write((json.dumps({"type": message}) + "\n").encode("utf-8"))

    def activate(self):
        if sys.platform == "win32" and self.process.processId():
            # Grant foreground activation only to our own editor process.
            ctypes.windll.user32.AllowSetForegroundWindow(int(self.process.processId()))
        self.send("activate")

    def close(self):
        self.activate()
        self.send("close")

    def read_messages(self):
        self.buffer += bytes(self.process.readAllStandardOutput())
        while b"\n" in self.buffer:
            line, self.buffer = self.buffer.split(b"\n", 1)
            try:
                message = json.loads(line)
            except (ValueError, UnicodeError):
                continue
            if not isinstance(message, dict):
                continue
            kind = message.get("type")
            if kind == "dirty":
                self.dirty = message.get("dirty") is True
            elif kind == "saved":
                self.dirty = False
                self.saved.emit()
            elif kind == "closeCancelled":
                self.closeCancelled.emit()

    def process_finished(self, code, status):
        self.read_messages()
        if status == QProcess.CrashExit or code != 0:
            self.failed.emit("WinUI 编辑器意外退出。此前已保存的课表仍在 JSON 文件中。")
        self.finished.emit()

    def process_error(self, error):
        if error == QProcess.FailedToStart:
            self.failed.emit("无法启动 WinUI 编辑器：" + self.process.errorString())
            self.finished.emit()

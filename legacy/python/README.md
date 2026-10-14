# Python 旧版

此目录保留迁移前的 Python 桌面课表源码、测试和独立 JSON 快照。当前正式程序已迁移到 C# / WinUI 3，构建与发布说明见仓库根目录 [README.md](../../README.md) 和 [`docs/updates.md`](../../docs/updates.md)。本目录不参与当前版本的构建或自动更新包制作。

## 运行

需要 Windows、Python 和 PyQt5。在本目录运行：

```powershell
python -m pip install -r requirements.txt
python app.py
```

也可以从仓库根目录运行 `view-legacy.cmd`。脚本要求系统中可以直接调用 `python`，并且已安装 PyQt5。

源码运行时，旧版从本目录读取 `timetable.json` 和 `exceptions.json`；缺少或格式无效时会报错，不会从当前 C# 版或内置数据恢复。此处的 JSON 是旧版自己的数据副本，与仓库根目录及 `release/latest/`、版本归档目录中的文件相互独立。

旧版主窗口和测试时间工具由 Python / PyQt5 提供。点击编辑课表会启动 C# / WinUI 编辑器；要在旧版中使用该功能，需要先在开发电脑构建 `winui-editor/`。直接查看编辑器可在仓库根目录运行：

```powershell
./winui-editor/run-editor.ps1
```

旧版支持使用 `--windowed` 打开普通调试窗口，`--preview-day 1` 到 `--preview-day 7` 预览星期，`--no-save` 禁用位置设置读写。程序位置和运行日志保存在本目录的 `user-data/`。

`DesktopTimetable-v8.spec` 和 `DesktopTimetable-v9.spec` 是历史 PyInstaller 配置，不是当前 C# 便携版的构建入口。旧版测试位于本目录的 `tests/`，可在本目录运行：

```powershell
python -m unittest discover -s tests -v
```

# 桌面课表

一个放在 Windows 桌面上的课表工具，自动显示当天安排，并标出正在上课或即将开始的课程

主程序使用 C#、.NET 8 和 WinUI 3，课表编辑器、托盘菜单和测试时钟都集成在同一个程序里

## 怎么用

打开便携版文件夹，双击 `DesktopTimetable.exe` 即可启动

支持 Windows 10 1809 及以上的 x64 系统，推荐 Windows 11

课表会留在桌面上，不遮挡其他窗口，按 Win+D 也不会消失

右上角有三个按钮：

| 按钮 | 用途 |
| --- | --- |
| GitHub | 打开[项目仓库](https://github.com/FylzX/timetable-for-seewo) |
| 铅笔 | 编辑课表 |
| 锁 | 锁定或解锁窗口的位置和大小 |

解锁后可以拖动窗口，拖动边缘或四角可以调整大小，字号会跟着窗口一起缩放

右键课表或托盘图标，还可以预览其他星期的课表、调整测试时间、恢复默认位置和退出程序

## 编辑课表

### 主课表

周一到周五各占一列，每天都能单独添加、修改和删除课程，手动添加时可以自由设置时间，不必和模板一致

点击课程卡片修改名称，点击时间调整时间段，鼠标移到卡片上会出现红色垃圾桶

空白列的中央有醒目的“添加课程”按钮，已有课程时，按钮放在列底部

各列共用时间轴，相同时间横向对齐，课程太多时可以滚动查看，也可以点击上下箭头翻动

### 例外日期

用于调课、节假日或临时安排，添加日期时可以复制主课表中某一天的课程

例外课表会替换该日期原本的安排，保留一个没有课程的例外日期，就表示当天放假；删除这个日期，则恢复使用主课表

每个日期都有自己的添加课程和删除日期按钮，日期较多时可以左右滚动，删除整天的课程前会要求确认

### 时间模板

左侧是两个页面共用的时间模板，可以收起，也可以随时展开修改

先设置好一组上课时间，再选择应用到所有工作日、某个工作日或某个例外日期，填课程名称时就不用重复输入时间

应用模板会替换目标列的全部课程，目标已有内容时会先询问是否覆盖

### 保存

改完后点击“保存课表”，桌面上的课表会立即更新

时间重叠、格式不对或课程名称没填完时，程序会提示修正；删除课程时会保留滚动位置，方便连续操作

有未保存的修改时关闭编辑器或退出程序，可以选择保存、放弃修改或继续编辑

## 测试日期和时间

右键选择“调整测试时间”，可以修改日期和时间，也可以直接拖动钟表上的时针、分针

启用后，课表按模拟时间运行，不会改动电脑的系统时钟，关闭测试窗口也不会停止模拟

点击“恢复电脑时间”或重启程序，即可回到正常显示

## 便携版和数据

便携版的目录结构如下：

```text
release/
  DesktopTimetable.exe     启动入口
  Portable.txt             使用说明
  timetable.json           主课表
  exceptions.json          例外日期
  template.json            时间模板
  app/                     主程序和运行库
  user-data/               窗口设置及日志
```

迁移电脑时复制整个文件夹，课表和窗口设置都会一起带走，可以放在自己选择的目录

`app/` 中的运行库和界面资源也是程序的一部分，请和启动入口一起保留，其中的 `resources.pri` 用于加载界面

三个 JSON 文件缺失时会自动补齐，已有文件不会被覆盖，模板缺失时会从已有课表提取时间，没有课程则创建空模板

| 文件 | 内容 |
| --- | --- |
| `timetable.json` | 用 `"1"` 到 `"7"` 表示周一至周日 |
| `exceptions.json` | 用 `YYYY-MM-DD` 表示例外日期，空数组表示当天放假 |
| `template.json` | 只保存开始和结束时间，不保存课程名称 |

通常直接用编辑器修改就够了，也可以手动编辑 JSON，例如主课表中的一节课：

```json
{
  "1": [
    { "start": "08:00", "end": "08:40", "course": "语文" }
  ]
}
```

时间使用 `HH:mm` 格式，同一天的课程按开始时间排列，不能重叠，修改文件后程序会自动重新读取

程序默认读取启动入口旁边的数据，也可以通过 `--data-dir` 指定目录；启动出错时可查看 `user-data/startup.log`

## 从源码运行或构建

开发需要 .NET 8 SDK，首次构建会从 NuGet 下载依赖

双击根目录的 `view-csharp.cmd` 可以从源码运行，`view-editor.cmd` 则只打开编辑器

生成便携版：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./build-portable.ps1
```

输出放在 `release/`，构建完成后手动打开 EXE 即可

重新构建会更新程序文件，保留 `release/` 里已有的课表和窗口设置，首次构建会复制仓库根目录的初始课表

也可以用 `-OutputDirectory` 输出到 `release/` 的子目录，方便单独试用

## 源码目录

| 目录或文件 | 内容 |
| --- | --- |
| `winui-app/` | 桌面课表、托盘、测试时钟和便携启动器 |
| `winui-editor/` | 课表编辑器，主程序直接复用这里的源码 |
| `tests/` | 数据、运行逻辑及界面检查 |
| `legacy/python/` | Python 旧版源码、依赖、打包配置和课表快照 |
| `dist/` | 本地保留的 V8 成品 |
| `build-portable.ps1` | 便携版构建脚本 |

Python 旧版使用自己的课表，与新版的数据分别保存，想运行旧版可安装依赖后双击 `view-legacy.cmd`

```powershell
cd legacy/python
python -m pip install -r requirements.txt
python app.py
```

## 检查

检查数据读写、时间轴和运行逻辑：

```powershell
dotnet run --project tests/winui-core/WinUiCoreChecks.csproj
```

构建便携版后，可以检查实际窗口启动、鼠标交互、桌面层级和外观：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tests/check-startup.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tests/check-startup.ps1 -Interactions
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tests/check-desktop-layer.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tests/check-widget-appearance.ps1
```

界面检查使用测试数据，不修改实际课表，部分检查会临时移动鼠标或切换桌面，请依次运行，结束后窗口会自动关闭

外观截图保存在 `test-output/widget-appearance/`，需要检查其他发布目录时可传入 `-ReleaseDirectory`

## Git 中保留什么

源码、测试、文档、项目配置、构建脚本和初始课表都提交，图标 `calendar.ico` 和 Python 的 `.spec` 打包配置也需要保留

`release/`、`dist/`、构建缓存、运行设置、日志和测试产物已加入 `.gitignore`，便携成品可以通过 GitHub Releases 分发

仓库根目录的 JSON 是初始数据，实际使用后修改的课表放在 `release/` 中，不会进入 Git
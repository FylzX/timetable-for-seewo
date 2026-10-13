using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using System.Text.Json;
using Windows.Graphics;
using WinRT.Interop;

namespace Timetable.Editor;

public sealed partial class MainWindow : Window
{
    private static readonly string[] Weekdays = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日"];
    private static readonly string[] Courses = ["语文", "数学", "英语", "体育", "物理", "化学", "等级组合1", "等级组合2", "等级组合3"];
    private readonly ScheduleFiles files;
    private readonly Bridge bridge;
    private EditorData data = new();
    private string baseline = "";
    private Dictionary<string, string> diskBaseline = [];
    private readonly string directory;
    private bool ready, approvedClose, closing, dialogOpen;
    private bool renderingTheme;
    private bool Dirty => ready && ScheduleFiles.Fingerprint(data) != baseline;
    public event Action? Saved;
    public bool HasUnsavedChanges => Dirty;

    public MainWindow(string directory, bool connected)
    {
        InitializeComponent();
        ExceptionBoardHost.SizeChanged += ExceptionHostSizeChanged;
        Title = "编辑课表";
        this.directory = directory;
        files = new ScheduleFiles(directory);
        bridge = new Bridge(connected);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        if (MicaControllerSupported()) SystemBackdrop = new MicaBackdrop();
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        var scale = NativeWindow.Dpi(hwnd) / 96.0;
        var width = Math.Min((int)(1420 * scale), area.Width - 40);
        var height = Math.Min((int)(940 * scale), area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
        AppWindow.Closing += WindowClosing;
        Root.ActualThemeChanged += (_, _) =>
        {
            if (!ready || renderingTheme) return;
            renderingTheme = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                try { RenderWeek(); RenderExceptions(); }
                finally { renderingTheme = false; }
            });
        };
        bridge.Start(command => DispatcherQueue.TryEnqueue(() =>
        {
            if (command == "activate") { Activate(); NativeWindow.BringToFront(hwnd); }
            if (command == "close") Close();
        }));
        try
        {
            data = files.Read();
            baseline = ScheduleFiles.Fingerprint(data);
            diskBaseline = ReadDiskState();
            ready = true;
            RenderTemplates(); RenderWeek(); RenderExceptions(); Changed();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            SaveButton.IsEnabled = false;
            Tabs.IsEnabled = false;
            ShowNotice("无法打开课表", error.Message, InfoBarSeverity.Error);
        }
        bridge.Send("ready");
    }

    private static bool MicaControllerSupported() => Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported();
    public static string WeekdayName(string key) => Weekdays[int.Parse(key) - 1];
    private Brush Brush(string name) => (Brush)Application.Current.Resources[name];

    private static TextBlock Text(string text, double size = 14, bool strong = false) => new()
    { Text = text, FontSize = size, FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
      TextWrapping = TextWrapping.Wrap };

    private Button ActionButton(string text, RoutedEventHandler click, bool accent = false)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += click;
        return button;
    }

    private void Changed()
    {
        var dirty = Dirty;
        SaveButton.IsEnabled = ready && dirty;
        Status.Text = dirty ? "有未保存的修改 · 保存后立即更新桌面课表" : "所有修改已保存";
        StatusIcon.Glyph = dirty ? "\uE70F" : "\uE73E";
        bridge.Send("dirty", dirty);
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title; Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true;
    }

    private static void Sort(List<Lesson> rows) => rows.Sort((a, b) => string.CompareOrdinal(a.Start, b.Start));

    private async Task<ContentDialogResult> Dialog(ContentDialog dialog)
    {
        if (dialogOpen) return ContentDialogResult.None;
        dialogOpen = true;
        try { dialog.XamlRoot = Root.XamlRoot; dialog.RequestedTheme = Root.ActualTheme; return await dialog.ShowAsync(); }
        finally { dialogOpen = false; }
    }

    private async Task<bool> Confirm(string title, string message, string action) =>
        await Dialog(new ContentDialog { Title = title, Content = Text(message), PrimaryButtonText = action,
            CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }) == ContentDialogResult.Primary;

    private async Task<Lesson?> EditTime(Lesson? row, List<Lesson> rows, bool includeCourse = false)
    {
        var start = row == null ? new TimeSpan(8, 0, 0) : TimeSpan.ParseExact(row.Start, "hh\\:mm", CultureInfo.InvariantCulture);
        var end = row == null ? new TimeSpan(8, 40, 0) : TimeSpan.ParseExact(row.End, "hh\\:mm", CultureInfo.InvariantCulture);
        if (row == null && rows.Count > 0)
        {
            var last = TimeSpan.ParseExact(rows[^1].End, "hh\\:mm", CultureInfo.InvariantCulture);
            if (last.TotalMinutes + 50 < 1440) { start = last.Add(TimeSpan.FromMinutes(10)); end = start.Add(TimeSpan.FromMinutes(40)); }
        }
        var body = new StackPanel { Spacing = 16 };
        ComboBox? course = null;
        TextBox? custom = null;
        if (includeCourse)
        {
            course = new ComboBox { Header = "课程", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var name in Courses) course.Items.Add(name);
            course.Items.Add("自定义课程…");
            custom = new TextBox { Header = "课程名称", PlaceholderText = "输入课程名称", Text = row?.Course ?? "", MaxLength = 80 };
            var selected = Array.IndexOf(Courses, row?.Course ?? "");
            course.SelectedIndex = selected >= 0 ? selected : Courses.Length;
            custom.Visibility = course.SelectedIndex == Courses.Length ? Visibility.Visible : Visibility.Collapsed;
            course.SelectionChanged += (_, _) =>
                custom.Visibility = course.SelectedIndex == Courses.Length ? Visibility.Visible : Visibility.Collapsed;
            body.Children.Add(course); body.Children.Add(custom);
        }
        var from = new TimeEntry("开始时间", start);
        var until = new TimeEntry("结束时间", end);
        body.Children.Add(from); body.Children.Add(until);
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        body.Children.Add(error);
        var dialog = new ContentDialog { Title = includeCourse ? (row == null ? "添加课程" : "编辑课程")
            : (row == null ? "添加时间段" : "调整时间段"), Content = body,
            PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        Lesson? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            void Reject(string message) { args.Cancel = true; error.Message = message; error.IsOpen = true; }
            if (!from.TryGetTime(out var fromTime)) { Reject("开始时间无效，请输入 00:00～23:59，例如 08:30。"); return; }
            if (!until.TryGetTime(out var untilTime)) { Reject("结束时间无效，请输入 00:00～23:59，例如 09:10。"); return; }
            if (fromTime >= untilTime) { Reject("结束时间必须晚于开始时间。"); return; }
            var name = includeCourse ? (course!.SelectedIndex == Courses.Length ? custom!.Text.Trim()
                : course.SelectedItem as string ?? "") : row?.Course ?? "";
            if (includeCourse && string.IsNullOrWhiteSpace(name)) { Reject("请选择课程或输入自定义课程名称。"); return; }
            result = new Lesson { Start = fromTime.ToString("hh\\:mm"), End = untilTime.ToString("hh\\:mm"), Course = name };
        };
        while (await Dialog(dialog) == ContentDialogResult.Primary && result != null)
        {
            var conflicts = rows.Where(existing => existing != row && ScheduleFiles.Overlaps(existing, result)).ToArray();
            if (conflicts.Length == 0 || await Confirm("时间重叠，仍要继续？",
                "当前时间与以下时间段重叠：\n" + string.Join("\n", conflicts.Select(existing => existing.TimeLabel
                    + (string.IsNullOrWhiteSpace(existing.Course) ? "" : " · " + existing.Course)))
                + "\n\n重叠课程可能相互遮挡，不方便点击、编辑或删除。确认后仍可保存。", "保留重叠"))
                return result;
            // The warning opens after the editor closes: WinUI allows only one
            // ContentDialog at a time. Going back preserves all entered fields.
            result = null;
        }
        return null;
    }

    private async Task ApplyTemplate(bool exceptions, string[] keys)
    {
        if (data.Template.Count == 0) { ShowNotice("模板为空", "请先添加至少一个时间段。", InfoBarSeverity.Warning); return; }
        var target = exceptions ? data.Exceptions : data.Weekly;
        var occupied = keys.Where(key => target[key].Count > 0).ToArray();
        if (occupied.Length > 0 && !await Confirm("覆盖当前内容？", "应用模板会清空当前内容，并重新创建等待选择课程的卡片。\n\n将覆盖：" +
            string.Join("、", occupied.Select(key => exceptions ? key : WeekdayName(key))), "确认覆盖")) return;
        foreach (var key in keys) target[key] = data.Template.Select(row => row.Copy(true)).ToList();
        if (exceptions) RenderExceptions(); else RenderWeek();
        Changed();
    }

    private async void AddDateClicked(object sender, RoutedEventArgs args)
    {
        var body = new StackPanel { Spacing = 16 };
        var day = new CalendarDatePicker { Header = "例外日期", Date = DateTimeOffset.Now, DateFormat = "{year.full}-{month.integer(2)}-{day.integer(2)}",
            HorizontalAlignment = HorizontalAlignment.Stretch };
        var source = new ComboBox { Header = "初始课程", HorizontalAlignment = HorizontalAlignment.Stretch };
        source.Items.Add("空白课表（当天放假）");
        for (var i = 0; i < 5; i++) source.Items.Add("复制主课表 · " + Weekdays[i]);
        source.SelectedIndex = 0;
        body.Children.Add(day); body.Children.Add(source);
        var error = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error }; body.Children.Add(error);
        var dialog = new ContentDialog { Title = "添加例外日期", Content = body, PrimaryButtonText = "添加日期", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, eventArgs) =>
        {
            if (day.Date == null || data.Exceptions.ContainsKey(day.Date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            { eventArgs.Cancel = true; error.Message = day.Date == null ? "请选择日期。" : "这个日期已经存在。"; error.IsOpen = true; }
        };
        if (await Dialog(dialog) != ContentDialogResult.Primary || day.Date == null) return;
        var key = day.Date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        data.Exceptions.Add(key, source.SelectedIndex == 0 ? [] : data.Weekly[source.SelectedIndex.ToString()].Select(row => row.Copy()).ToList());
        RenderExceptions(); RenderTemplates(); Changed();
        DispatcherQueue.TryEnqueue(() => { exceptionScroll?.UpdateLayout(); exceptionScroll?.ChangeView(exceptionScroll.ScrollableWidth, null, null); });
    }

    private Dictionary<string, string> ReadDiskState() => new[] { "timetable.json", "exceptions.json", "template.json" }
        .ToDictionary(name => name, name => File.Exists(Path.Combine(directory, name))
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))) : "missing");

    private async Task<bool> Save()
    {
        try
        {
            // Validate before the external-file conflict prompt. A malformed edit never
            // reaches disk and never asks the user to overwrite someone else's changes.
            ScheduleFiles.ValidateForSave(data);
            if (ReadDiskState().Any(entry => diskBaseline[entry.Key] != entry.Value) &&
                !await Confirm("文件已被其他程序修改", "打开编辑器后课表文件发生了变化。保存将覆盖文件中的修改，是否继续？", "覆盖并保存")) return false;
            files.Save(data);
            baseline = ScheduleFiles.Fingerprint(data); diskBaseline = ReadDiskState(); Changed();
            bridge.Send("saved"); Notice.IsOpen = false; Saved?.Invoke(); return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            ShowNotice("尚未保存", error.Message + "。修改仍保留在编辑器中。", InfoBarSeverity.Error);
            return false;
        }
    }

    private async void SaveClicked(object sender, RoutedEventArgs args) => await Save();
    private async void WindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (approvedClose || !Dirty) { bridge.Send("closed"); return; }
        args.Cancel = true;
        await RequestCloseAsync();
    }

    public async Task<bool> RequestCloseAsync()
    {
        if (approvedClose || !Dirty) { Close(); return true; }
        if (closing || dialogOpen) { bridge.Send("closeCancelled"); return false; }
        closing = true;
        Activate();
        try
        {
            // Animate the content so maximized windows keep their placement.
            var transform = new TranslateTransform(); Root.RenderTransform = transform;
            foreach (var offset in new[] { -8, 8, -5, 5, 0 })
            { transform.X = offset; await Task.Delay(40); }
            Root.RenderTransform = null;
            var result = await Dialog(new ContentDialog { Title = "课表还没有保存", Content = Text("保存修改，还是放弃本次编辑？"),
                PrimaryButtonText = "保存并关闭", SecondaryButtonText = "放弃修改", CloseButtonText = "继续编辑",
                DefaultButton = ContentDialogButton.Close });
            if (result == ContentDialogResult.Secondary || (result == ContentDialogResult.Primary && await Save()))
            { approvedClose = true; Close(); return true; }
            bridge.Send("closeCancelled");
            return false;
        }
        finally { closing = false; }
    }
}

using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using System.Text.Json;
using Timetable.Editor;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Timetable.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly string directory;
    private readonly ScheduleFiles files;
    private readonly SimulationClock clock = new();
    private readonly DispatcherTimer timer;
    private readonly NativeWidget native;
    private readonly nint hwnd;
    private readonly InputNonClientPointerSource pointerSource;
    private readonly string statePath;
    private readonly DesktopSettings settings;
    private readonly CancellationTokenSource updateCheckCancellation = new();
    private EditorData data = new();
    private Timetable.Editor.MainWindow? editor;
    private TestTimeWindow? testWindow;
    private UpdateWindow? updateWindow;
    private MenuFlyout? updateMenuFlyout;
    private DispatcherTimer? updateCheckTimer;
    private bool updateCheckRunning, newerVersionAvailable, updateBadgeEnabled = true;
    private int? previewDay;
    private bool locked, dragging, resizing, closing, approvedClose, windowClosed;
    private int resizeEdges;
    private PointInt32 dragOrigin;
    private RectInt32 windowOrigin;
    private string lastDataStamp = "", renderStamp = "";
    private string? loadError;
    private Placement placement = new();
    private RectInt32 workArea;

    public MainWindow(string directory)
    {
        StartupLog.Write("MainWindow.InitializeComponent");
        InitializeComponent();
        StartupLog.Write("MainWindow XAML initialized");
        Title = "桌面课表 v" + UpdateClient.CurrentVersion;
        this.directory = directory;
        files = new ScheduleFiles(directory);
        statePath = Path.Combine(directory, "user-data", "settings-csharp.json");
        settings = new DesktopSettings(directory);
        hwnd = WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new TransparentBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            // All resizing uses our pointer handlers, so the native frame stays off.
            presenter.IsResizable = false;
        }
        NativeWidget.RemoveWindowFrame(hwnd);
        pointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        RestorePlacement();
        StartupLog.Write("Window placement restored");
        AppWindow.Closing += WindowClosing;
        native = new NativeWidget(hwnd, command => DispatcherQueue.TryEnqueue(() => TrayAction(command)),
            () => DispatcherQueue.TryEnqueue(RestorePlacement), NativeCursorEdges);
        StartupLog.Write("Tray initialized");
        LoadData();
        StartupLog.Write("Schedule loaded");
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Closed += (_, _) =>
        {
            windowClosed = true; timer.Stop(); updateCheckTimer?.Stop(); updateCheckCancellation.Cancel(); updateCheckCancellation.Dispose();
            native.Dispose(); testWindow?.Close(); updateWindow?.Close();
        };
        Root.RightTapped += (_, args) => { ShowMenu(args.GetPosition(Root)); args.Handled = true; };
        Root.PointerPressed += StartDrag;
        Root.PointerMoved += MoveDrag;
        Root.PointerReleased += EndDrag;
        Root.PointerCaptureLost += (_, _) => { dragging = resizing = false; Root.SetPointerShape(InputSystemCursorShape.Arrow); };
        Root.PointerExited += (_, _) => { if (!resizing) Root.SetPointerShape(InputSystemCursorShape.Arrow); };
        Root.SizeChanged += (_, _) =>
        {
            pointerSource.SetRegionRects(NonClientRegionKind.Passthrough, [new RectInt32(0, 0, AppWindow.Size.Width, AppWindow.Size.Height)]);
            FitRows();
        };
        RowsHost.SizeChanged += (_, _) => FitRows();
        if (!Environment.GetCommandLineArgs().Any(argument => argument is "--smoke-test" or "--interaction-test" or "--layer-test"))
            StartAutomaticUpdateChecks();
    }

    // Call after Activate: Explorer ownership should be established on the visible HWND.
    public void AttachToDesktop()
    {
        if (!Environment.GetCommandLineArgs().Contains("--windowed")) native.AttachDesktop();
    }

    // Checks the actual published XAML and window lifetime, without saving edits.
    internal async Task CheckInteractionsAsync()
    {
        await Task.Delay(500);
        if (loadError != null) throw new InvalidOperationException(loadError);
        if (Environment.GetCommandLineArgs().Contains("--windowed")) await CheckResizeCursorsAsync();
        ((IInvokeProvider)new ButtonAutomationPeer(UpdateMenuButton).GetPattern(PatternInterface.Invoke)).Invoke();
        StartupLog.Write("Update menu button opened without error");
        updateMenuFlyout?.Hide();
        EditClicked(this, new RoutedEventArgs());
        await Task.Delay(700);
        await editor!.CheckEditorTabsAsync();
        StartupLog.Write("Editor tab buttons switched to their matching pages");
        await editor!.CheckTimeEntryAsync();
        StartupLog.Write("Typed and selected times stayed synchronized; invalid input remained invalid");
        await editor!.CheckScrollDeletionAsync();
        StartupLog.Write("Repeated deletion preserved main and exception scroll positions");
        var savedOpacity = settings.ReadBackgroundOpacity();
        void CheckBackground(byte expected)
        {
            if (Surface.Background is not SolidColorBrush brush || brush.Color.A != expected || Surface.Opacity != 1)
                throw new InvalidOperationException("Background preview changed the wrong surface or alpha.");
        }
        newerVersionAvailable = true;
        RefreshUpdateBadge();
        await editor.CheckMenuSettingsAsync(enabled =>
        {
            UpdateBadgePreferenceChanged(enabled);
            if (UpdateBadge.Visibility != (enabled ? Visibility.Visible : Visibility.Collapsed))
                throw new InvalidOperationException("The update badge did not immediately follow its setting.");
        });
        newerVersionAvailable = false;
        RefreshUpdateBadge();
        await editor.CheckAppearanceAsync(CheckBackground);
        await editor.DiscardAppearanceCheckAsync();
        CheckBackground((byte)Math.Round(savedOpacity / 100 * 255));
        EditClicked(this, new RoutedEventArgs());
        await Task.Delay(200);
        if (Math.Abs(editor!.BackgroundOpacityPreview - savedOpacity) > .000001 || editor.HasUnsavedChanges)
            throw new InvalidOperationException("Reopening the editor did not restore saved appearance.");
        if (!await editor.RequestCloseAsync()) throw new InvalidOperationException("Diagnostic editor did not close.");
        StartupLog.Write("Appearance preview, save, exact default, discard and reopen checked");
        approvedClose = true;
        Close();
    }

    private async Task CheckResizeCursorsAsync()
    {
        var position = NativeWidget.Cursor();
        var wasLocked = locked;
        try
        {
            locked = false; UpdateLock();
            NativeWidget.BringToFront(hwnd);
            NativeWidget.RaiseForDiagnostic(hwnd);
            var rect = WindowRect;
            var scale = NativeWidget.Dpi(hwnd) / 96.0;
            var inset = (int)(9 * scale);
            var left = rect.X + inset; var right = rect.X + rect.Width - inset;
            var top = rect.Y + inset; var bottom = rect.Y + rect.Height - inset;
            var midX = rect.X + rect.Width / 2; var midY = rect.Y + rect.Height / 2;
            foreach (var (x, y, cursor) in new[] { (left, midY, 32644), (right, midY, 32644),
                (midX, top, 32645), (midX, bottom, 32645), (left, top, 32642), (right, bottom, 32642),
                (right, top, 32643), (left, bottom, 32643) })
            {
                NativeWidget.MoveCursor(new PointInt32(x, y));
                await Task.Delay(150);
                if (!NativeWidget.CursorMatches(cursor)) throw new InvalidOperationException($"Incorrect resize cursor at {x},{y} (expected {cursor}, XAML={Root.PointerShape}, root={Root.ActualWidth}x{Root.ActualHeight}, dpi={scale}, hwnd={hwnd}, rect={rect.X},{rect.Y},{rect.Width},{rect.Height}): {NativeWidget.CursorDescription(cursor)}");
            }
            locked = true; UpdateLock();
            NativeWidget.MoveCursor(new PointInt32(left, midY));
            await Task.Delay(150);
            if (!NativeWidget.CursorMatches(32512)) throw new InvalidOperationException("Locked widget still shows a resize cursor.");
            StartupLog.Write("All eight resize cursors and locked arrow checked");
        }
        finally { NativeWidget.MoveCursor(position); locked = wasLocked; UpdateLock(); }
    }

    internal async Task CheckStartupAsync()
    {
        await Task.Delay(1000);
        if (Root.XamlRoot == null || Root.ActualWidth <= 0 || loadError != null)
            throw new InvalidOperationException("Main window failed startup validation: " + loadError);
        EditClicked(this, new RoutedEventArgs());
        await Task.Delay(1000);
        var editorRoot = editor?.Content as FrameworkElement;
        if (editorRoot?.XamlRoot == null || editorRoot.ActualWidth <= 0)
            throw new InvalidOperationException("Editor did not load.");
        if (editorRoot.FindName("Tabs") is not Grid tabs || !tabs.IsHitTestVisible)
            throw new InvalidOperationException("Editor could not read the schedule.");
        if (editor?.SelectedTabIndex != 0 || editorRoot.FindName("ContextMenuOptionsList") is not ListView menuOptions
            || menuOptions.Items.Count != DesktopSettings.ConfigurableMenuItems.Length)
            throw new InvalidOperationException("Editor did not open on the main timetable or load all context menu settings.");
        StartupLog.Write("Editor main timetable loaded");
        await editor.CheckEditorTabsAsync();
        StartupLog.Write("Editor tab buttons switched to their matching pages");
        editor.SelectEditorTab(1);
        await Task.Delay(500);
        StartupLog.Write("Editor exception dates loaded");
        editor.SelectEditorTab(2);
        await Task.Delay(200);
        if (editorRoot.FindName("BackgroundOpacitySlider") is not Slider slider || slider.ActualWidth <= 0)
            throw new InvalidOperationException("Appearance settings did not load.");
        StartupLog.Write("Editor appearance settings loaded");
        OpenTestTime();
        await Task.Delay(1000);
        if (testWindow?.Content is not FrameworkElement testRoot || testRoot.XamlRoot == null || testRoot.ActualWidth <= 0)
            throw new InvalidOperationException("Test clock did not load.");
        StartupLog.Write("Test clock loaded");
        testWindow.Close();
        if (!await editor!.RequestCloseAsync()) throw new InvalidOperationException("Unedited editor did not close.");
        var commandLine = Environment.GetCommandLineArgs();
        var updateCheck = Array.IndexOf(commandLine, "--update-ui-remote");
        if (updateCheck >= 0 && updateCheck + 1 < commandLine.Length)
        {
            if (!commandLine.Contains("--update-test-url")) throw new InvalidOperationException("更新界面检查需要本机模拟服务。");
            CheckUpdates();
            await Task.Delay(300);
            await updateWindow!.CheckPresentationAsync(commandLine[updateCheck + 1]);
            StartupLog.Write("Update window status and local/remote versions verified");
            updateWindow.Close();
        }
        approvedClose = true;
        Close();
    }

    private DisplayArea CurrentDisplay => DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd), DisplayAreaFallback.Primary);
    private RectInt32 WindowRect => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    private void RestorePlacement()
    {
        try
        {
            if (File.Exists(statePath)) placement = JsonSerializer.Deserialize<Placement>(File.ReadAllText(statePath)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { placement = new(); }
        var monitor = DisplayArea.GetFromPoint(new PointInt32(placement.MonitorX + 1, placement.MonitorY + 1), DisplayAreaFallback.Primary);
        workArea = monitor.WorkArea;
        var rect = placement.Restore(workArea.X, workArea.Y, workArea.Width, workArea.Height);
        AppWindow.MoveAndResize(new RectInt32(rect.X, rect.Y, rect.Width, rect.Height));
        locked = placement.Locked;
        UpdateLock();
        try { ApplyBackgroundOpacity(editor?.BackgroundOpacityPreview ?? settings.ReadBackgroundOpacity()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { ApplyBackgroundOpacity(DesktopSettings.DefaultBackgroundOpacity); }
    }

    private void ApplyBackgroundOpacity(double opacity) => Surface.Background = new SolidColorBrush(
        Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 100) / 100 * 255), 0x15, 0x15, 0x1E));

    private void SavePlacement()
    {
        var rect = WindowRect;
        workArea = CurrentDisplay.WorkArea;
        placement = Placement.Capture(rect.X, rect.Y, rect.Width, rect.Height, workArea.X, workArea.Y, workArea.Width, workArea.Height, locked);
        try
        {
            settings.SavePlacement(placement);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { loadError = "窗口位置暂时无法保存：" + error.Message; }
    }

    private string DataStamp() => string.Join("|", new[] { "timetable.json", "exceptions.json", "template.json" }.Select(name =>
    {
        var info = new FileInfo(Path.Combine(directory, name));
        return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "missing";
    }));

    private void LoadData()
    {
        try { data = files.Read(); loadError = null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { loadError = "课表文件错误：" + error.Message; }
        finally { lastDataStamp = DataStamp(); }
        renderStamp = "";
        Render();
    }

    private void Tick()
    {
        if (windowClosed || approvedClose) return;
        if (DataStamp() != lastDataStamp) LoadData();
        // Explorer might have been replaced while the widget was running.
        if (!native.DesktopAttached) AttachToDesktop();
        var currentWork = CurrentDisplay.WorkArea;
        if (!dragging && !resizing && (currentWork.Width != workArea.Width || currentWork.Height != workArea.Height)) RestorePlacement();
        Render();
    }

    private DateOnly ShownDate
    {
        get
        {
            var date = DateOnly.FromDateTime(clock.Now);
            return previewDay is int day ? date.AddDays(day - ((int)date.DayOfWeek + 6) % 7) : date;
        }
    }

    private void Render()
    {
        var now = clock.Now;
        var date = ShownDate;
        var rows = DailySchedule.Rows(data, date);
        var marker = DailySchedule.Marker(rows, date, now);
        var stamp = $"{date}:{marker}:{clock.Active}:{previewDay}:{lastDataStamp}:{loadError}";
        if (stamp == renderStamp) return;
        renderStamp = stamp;
        DateTitle.Text = $"{NativeWidget.DailyNames[((int)date.DayOfWeek + 6) % 7]}  {date:MM/dd}";
        Status.Text = loadError ?? string.Join(" · ", new[] { clock.Active ? "测试时间" : null, previewDay != null ? "课表预览" : null }.Where(text => text != null));
        Status.Visibility = Status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(Status, loadError);
        StateBadge.Visibility = marker is null ? Visibility.Collapsed : Visibility.Visible;
        StateText.Text = marker is { Live: true } ? "正在上课" : "即将开始";
        StateBadge.Background = new SolidColorBrush(marker is { Live: true } ? Color.FromArgb(235, 224, 4, 0) : Color.FromArgb(230, 68, 94, 132));
        RowsHost.Children.Clear(); RowsHost.RowDefinitions.Clear();
        if (rows.Count == 0)
        {
            RowsHost.Children.Add(new TextBlock { Text = loadError == null ? "今天没有课程" : "请检查课表文件", Style = (Style)Root.Resources["CourseTextStyle"], VerticalAlignment = VerticalAlignment.Top });
            FitRows();
            return;
        }
        for (var index = 0; index < rows.Count; index++)
        {
            RowsHost.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            var row = rows[index];
            var active = marker is { } marked && marked.Index == index;
            var panel = new Grid();
            panel.ColumnDefinitions.Add(new() { Width = new GridLength(.55, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new() { Width = new GridLength(.45, GridUnitType.Star) });
            var accent = new Border { IsHitTestVisible = false };
            Grid.SetColumnSpan(accent, 2); panel.Children.Add(accent);
            panel.Children.Add(new TextBlock { Text = row.TimeLabel, Style = (Style)Root.Resources["TimeTextStyle"] });
            var course = new TextBlock { Text = row.Course, Style = (Style)Root.Resources["CourseTextStyle"] };
            Grid.SetColumn(course, 1); panel.Children.Add(course);
            if (active)
            {
                var live = marker!.Value.Live;
                accent.Background = new SolidColorBrush(live ? Color.FromArgb(155, 158, 22, 29) : Color.FromArgb(115, 68, 94, 132));
                accent.BorderBrush = new SolidColorBrush(live ? Color.FromArgb(180, 245, 48, 40) : Color.FromArgb(110, 144, 176, 221));
                accent.Child = new Border { HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(live ? Color.FromArgb(255, 255, 42, 32) : Color.FromArgb(255, 155, 186, 230)) };
            }
            if (index < rows.Count - 1)
            {
                var rule = new Border { Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Bottom, Tag = "rule", IsHitTestVisible = false };
                Grid.SetColumnSpan(rule, 2); panel.Children.Add(rule);
            }
            Grid.SetRow(panel, index); RowsHost.Children.Add(panel);
        }
        FitRows();
    }

    private void FitRows()
    {
        var width = Root.ActualWidth;
        var height = Root.ActualHeight;
        if (width <= 0 || height <= 0) return;
        // Match the original QPainter layout in logical pixels, independently of DPI.
        var unit = Math.Min(width / 380, height / 632);
        var pad = 24 * unit;
        var contentWidth = width - 2 * pad;
        var timeWidth = contentWidth * .55;
        Surface.CornerRadius = new CornerRadius(16 * unit);
        void Place(FrameworkElement element, double x, double y, double w, double h)
        { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); element.Width = Math.Max(0, w); element.Height = Math.Max(0, h); }
        Place(DateTitle, pad, 17 * unit, width - pad - 142 * unit, 32 * unit);
        FitText(DateTitle, 24 * unit, DateTitle.Width, DateTitle.Height);
        Place(UpdateMenuButton, width - 138 * unit, 15 * unit, 36 * unit, 36 * unit);
        Place(EditButton, width - 96 * unit, 15 * unit, 36 * unit, 36 * unit);
        Place(LockButton, width - 54 * unit, 15 * unit, 36 * unit, 36 * unit);
        Place(UpdateBadge, width - 106 * unit, 12 * unit, 9 * unit, 9 * unit);
        UpdateMenuButton.CornerRadius = EditButton.CornerRadius = LockButton.CornerRadius = new CornerRadius(7 * unit);
        UpdateMenuButton.BorderThickness = EditButton.BorderThickness = LockButton.BorderThickness = new Thickness(Math.Max(.8, unit));
        UpdateMenuIcon.FontSize = 20 * unit;
        EditIcon.FontSize = LockIcon.FontSize = 20 * unit;
        Place(Status, pad, 49 * unit, Math.Min(180 * unit, contentWidth), 18 * unit);
        Status.FontSize = 10 * unit;
        Place(StateBadge, width - pad - 92 * unit, 51 * unit, 92 * unit, 20 * unit);
        StateBadge.CornerRadius = new CornerRadius(4 * unit);
        StateText.FontSize = 13 * unit;
        Place(TimeHeading, pad, 72 * unit, timeWidth, 26 * unit);
        Place(CourseHeading, pad + timeWidth, 72 * unit, contentWidth - timeWidth, 26 * unit);
        TimeHeading.FontSize = CourseHeading.FontSize = 16 * unit;
        Place(HeaderRule, pad, 104 * unit, contentWidth, Math.Max(.65, .7 * unit));
        Place(RowsHost, pad, 104 * unit, contentWidth, height - 128 * unit);
        Place(ResizeGrip, width - 22 * unit, height - 22 * unit, 13 * unit, 13 * unit);
        ResizeGrip.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        ResizeGrip.StrokeThickness = Math.Max(1, unit);
        if (RowsHost.RowDefinitions.Count == 0)
        {
            foreach (var text in RowsHost.Children.OfType<TextBlock>())
            {
                text.Margin = new Thickness(0, 30 * unit, 0, 0);
                text.Height = 44 * unit;
                FitText(text, 24 * unit, contentWidth, text.Height);
            }
            return;
        }
        var rowHeight = RowsHost.Height / RowsHost.RowDefinitions.Count;
        foreach (var panel in RowsHost.Children.OfType<Grid>())
        {
            foreach (var border in panel.Children.OfType<Border>())
                if (Equals(border.Tag, "rule")) border.Height = Math.Max(.5, .6 * unit);
                else
                {
                    border.Margin = new Thickness(-12 * unit, 2 * unit, -12 * unit, 2 * unit);
                    border.CornerRadius = new CornerRadius(5 * unit);
                    border.BorderThickness = new Thickness(Math.Max(.8, unit));
                    if (border.Child is Border stripe)
                    { stripe.Width = 7 * unit; stripe.CornerRadius = new CornerRadius(4 * unit, 0, 0, 4 * unit); }
                }
            foreach (var (text, index) in panel.Children.OfType<TextBlock>().Select((text, index) => (text, index)))
            {
                text.Margin = index == 0 ? new Thickness(0, 0, 10 * unit, 0) : new Thickness(0);
                FitText(text, (index == 0 ? 21 : 24) * unit, index == 0 ? timeWidth - 10 * unit : contentWidth - timeWidth, rowHeight);
            }
        }
    }

    private static void FitText(TextBlock text, double size, double width, double height)
    {
        text.FontSize = Math.Max(1, size);
        // An unconstrained measurement must not inherit the label's layout width.
        var probe = new TextBlock { Text = text.Text, FontSize = text.FontSize, FontFamily = text.FontFamily, FontWeight = text.FontWeight };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var fit = Math.Min(1, Math.Min(width / Math.Max(1, probe.DesiredSize.Width), height / Math.Max(1, probe.DesiredSize.Height)));
        text.FontSize = Math.Max(1, size * fit);
    }

    private void UpdateMenuClicked(object sender, RoutedEventArgs args)
    {
        var menu = updateMenuFlyout = new MenuFlyout();
        var check = new MenuFlyoutItem { Text = "检查更新", Icon = new FontIcon { Glyph = "\uE895", FontSize = 16 } };
        check.Click += (_, _) => CheckUpdates();
        var github = new MenuFlyoutItem { Text = "打开 GitHub", Icon = new FontIcon { Glyph = "\uE774", FontSize = 16 } };
        github.Click += async (_, _) => await OpenGitHubAsync();
        menu.Items.Add(check); menu.Items.Add(github);
        menu.ShowAt(UpdateMenuButton);
    }

    private async Task OpenGitHubAsync()
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/FylzX/timetable-for-seewo")))
                throw new InvalidOperationException("未能打开默认浏览器");
        }
        catch (Exception error)
        {
            Status.Text = "无法打开 GitHub 项目";
            Status.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(Status, error.Message);
        }
    }

    private void EditClicked(object sender, RoutedEventArgs args)
    {
        if (editor != null) { editor.Activate(); NativeWidget.BringToFront(WindowNative.GetWindowHandle(editor)); return; }
        editor = new Timetable.Editor.MainWindow(directory, false);
        editor.BackgroundOpacityPreviewChanged += ApplyBackgroundOpacity;
        editor.UpdateBadgePreferenceChanged += UpdateBadgePreferenceChanged;
        ApplyBackgroundOpacity(editor.BackgroundOpacityPreview);
        editor.Saved += LoadData;
        editor.Closed += (_, _) => { editor = null; LoadData(); };
        editor.Activate();
    }

    private void LockClicked(object sender, RoutedEventArgs args) { locked = !locked; UpdateLock(); SavePlacement(); }
    private void UpdateLock()
    {
        LockIcon.Glyph = locked ? "\uE72E" : "\uE785";
        ToolTipService.SetToolTip(LockButton, locked ? "解锁位置和大小" : "锁定位置和大小");
        ResizeGrip.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        if (locked) Root.SetPointerShape(InputSystemCursorShape.Arrow);
    }

    private void ShowMenu(Point point, bool expanded = false)
    {
        var menu = new MenuFlyout();
        FontIcon Icon(string glyph) => new() { Glyph = glyph, FontSize = 16 };
        void Compact(MenuFlyoutItemBase item)
        {
            // WinUI normally reserves separate slots for checks and icons when
            // both occur in a menu. Share one slot and align every text label.
            item.Resources["MenuFlyoutItemPlaceholderThemeThickness"] = new Thickness(0);
            item.Resources["MenuFlyoutItemDoublePlaceholderThemeThickness"] =
                new Thickness(28, 0, 0, 0);
        }
        void Add(string text, Action clicked, string? glyph = null)
        {
            var item = new MenuFlyoutItem { Text = text };
            if (glyph != null) item.Icon = Icon(glyph);
            Compact(item);
            item.Click += (_, _) => clicked(); menu.Items.Add(item);
        }
        DesktopPreferences preferences;
        try { preferences = settings.ReadPreferences(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            StartupLog.Write("Could not read context menu settings: " + error.Message);
            preferences = new(DesktopSettings.DefaultBackgroundOpacity,
                DesktopSettings.ConfigurableMenuItems.Select(item => item.Id).ToArray(), [], true);
        }
        updateBadgeEnabled = preferences.UpdateBadgeEnabled;
        RefreshUpdateBadge();
        var hidden = preferences.HiddenContextMenuItems.ToHashSet(StringComparer.Ordinal);
        foreach (var id in preferences.ContextMenuOrder)
            if (expanded || !hidden.Contains(id)) AddContextMenuItem(menu, id, Compact, Icon);
        if (hidden.Count > 0)
        {
            var expand = new MenuFlyoutItem { Text = expanded ? "折叠" : "展开" };
            Compact(expand);
            expand.Click += (_, _) =>
            {
                menu.Hide();
                DispatcherQueue.TryEnqueue(() => ShowMenu(point, !expanded));
            };
            menu.Items.Add(expand);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("退出", Close);
        menu.ShowAt(Root, point);
    }

    private void AddContextMenuItem(MenuFlyout menu, string id, Action<MenuFlyoutItemBase> compact, Func<string, FontIcon> icon)
    {
        void Add(string text, Action clicked, string? glyph = null)
        {
            var item = new MenuFlyoutItem { Text = text };
            if (glyph != null) item.Icon = icon(glyph);
            compact(item); item.Click += (_, _) => clicked(); menu.Items.Add(item);
        }
        switch (id)
        {
            case "edit": Add("编辑课表", () => EditClicked(this, new RoutedEventArgs()), "\uE713"); break;
            case "test-time": Add("调整时间(测试用)", OpenTestTime, "\uE787"); break;
            case "lock":
                var toggle = new ToggleMenuFlyoutItem { Text = "锁定位置和大小", IsChecked = locked };
                compact(toggle); toggle.Click += LockClicked; menu.Items.Add(toggle); break;
            case "view-schedule":
                var preview = new MenuFlyoutSubItem { Text = "查看课表", Icon = icon("\uE787") };
                compact(preview);
                var today = new ToggleMenuFlyoutItem { Text = "自动显示今天", IsChecked = previewDay == null };
                today.Click += (_, _) => { previewDay = null; Render(); }; preview.Items.Add(today);
                for (var day = 0; day < 7; day++)
                {
                    var value = day;
                    var item = new ToggleMenuFlyoutItem { Text = NativeWidget.DailyNames[day], IsChecked = previewDay == day };
                    item.Click += (_, _) => { previewDay = value; Render(); }; preview.Items.Add(item);
                }
                menu.Items.Add(preview); break;
            case "reset-placement": Add("恢复默认位置和大小", ResetPlacement, "\uE777"); break;
            case "reload-schedule": Add("重新读取课表", LoadData, "\uE72C"); break;
            case "check-updates": Add("检查更新", CheckUpdates, "\uE895"); break;
        }
    }

    private void ResetPlacement()
    {
        if (locked) return;
        var area = CurrentDisplay.WorkArea;
        var rect = new Placement().Restore(area.X, area.Y, area.Width, area.Height);
        AppWindow.MoveAndResize(new RectInt32(rect.X, rect.Y, rect.Width, rect.Height)); SavePlacement();
    }

    private void TrayAction(int command)
    {
        switch (command)
        {
            case 1001: EditClicked(this, new RoutedEventArgs()); break;
            case 1002: LoadData(); break;
            case 1003: OpenTestTime(); break;
            case 1004: Close(); break;
            case 1005: LockClicked(this, new RoutedEventArgs()); break;
            case 1006: ResetPlacement(); break;
            case 1007: CheckUpdates(); break;
            case 1009: previewDay = null; Render(); break;
            default: if (command is >= 1010 and <= 1016) { previewDay = command - 1010; Render(); } break;
        }
    }

    private void OpenTestTime()
    {
        if (testWindow is not null) { testWindow.Activate(); return; }
        testWindow = new TestTimeWindow(clock, Render);
        testWindow.Closed += (_, _) => testWindow = null;
        testWindow.Activate();
    }

    private void CheckUpdates()
    {
        newerVersionAvailable = false;
        RefreshUpdateBadge();
        if (updateWindow != null) { updateWindow.Activate(); return; }
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--update-test-url");
        Uri? testBase = index >= 0 && index + 1 < args.Length ? new Uri(args[index + 1]) : null;
        updateWindow = new UpdateWindow(testBase, async () =>
        {
            if (editor != null && !await editor.RequestCloseAsync()) return false;
            SavePlacement(); return true;
        }, () => { approvedClose = true; Close(); Application.Current.Exit(); });
        updateWindow.Closed += (_, _) => updateWindow = null;
        updateWindow.Activate();
    }

    private void StartAutomaticUpdateChecks()
    {
        try { updateBadgeEnabled = settings.ReadPreferences().UpdateBadgeEnabled; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { StartupLog.Write("Could not load update badge preference: " + error.Message); }
        RefreshUpdateBadge();
        updateCheckTimer = new DispatcherTimer { Interval = UpdateBadgePolicy.CheckInterval };
        updateCheckTimer.Tick += async (_, _) => await CheckUpdateAvailabilityAsync();
        updateCheckTimer.Start();
        _ = CheckUpdateAvailabilityAsync();
    }

    private async Task CheckUpdateAvailabilityAsync()
    {
        if (updateCheckRunning || windowClosed || updateCheckCancellation.IsCancellationRequested) return;
        updateCheckRunning = true;
        try
        {
            using var client = new UpdateClient();
            var result = await client.CheckAsync(UpdateClient.CurrentVersion, updateCheckCancellation.Token);
            newerVersionAvailable = result.Update != null;
            RefreshUpdateBadge();
            StartupLog.Write(newerVersionAvailable ? "Automatic update check found a newer release" : "Automatic update check found no newer release");
        }
        catch (OperationCanceledException) when (updateCheckCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            StartupLog.Write("Automatic update check failed: " + error.Message);
        }
        finally { updateCheckRunning = false; }
    }

    private void RefreshUpdateBadge() => UpdateBadge.Visibility = UpdateBadgePolicy.ShouldShow(newerVersionAvailable, updateBadgeEnabled)
        ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateBadgePreferenceChanged(bool enabled)
    {
        updateBadgeEnabled = enabled;
        RefreshUpdateBadge();
    }

    internal void WriteUpdateHealth(string path)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory).Parent!.FullName;
        var work = Path.Combine(root, ".update") + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path);
        if (!target.StartsWith(work, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(target) != work.TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(target).StartsWith("health-", StringComparison.Ordinal)) throw new InvalidDataException("启动检查路径无效。");
        if (loadError != null || Root.XamlRoot == null || Root.ActualWidth <= 0) throw new InvalidOperationException("新版启动检查失败：" + loadError);
        File.WriteAllText(target, UpdateClient.CurrentVersion);
    }

    private void StartDrag(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(Root);
        if (locked || !point.Properties.IsLeftButtonPressed) return;
        for (var source = args.OriginalSource as DependencyObject; source != null; source = VisualTreeHelper.GetParent(source))
            if (source is ButtonBase) return;
        resizeEdges = EdgesAt(point.Position);
        UpdateResizeCursor(resizeEdges);
        resizing = resizeEdges != 0; dragging = !resizing;
        dragOrigin = NativeWidget.Cursor(); windowOrigin = WindowRect;
        Root.CapturePointer(args.Pointer); args.Handled = true;
    }

    private void MoveDrag(object sender, PointerRoutedEventArgs args)
    {
        if (!dragging && !resizing)
        {
            UpdateResizeCursor(locked ? 0 : EdgesAt(args.GetCurrentPoint(Root).Position));
            return;
        }
        var cursor = NativeWidget.Cursor();
        var dx = cursor.X - dragOrigin.X; var dy = cursor.Y - dragOrigin.Y;
        if (dragging) AppWindow.Move(new PointInt32(windowOrigin.X + dx, windowOrigin.Y + dy));
        else
        {
            var minWidth = (int)(250 * NativeWidget.Dpi(hwnd) / 96.0);
            var minHeight = (int)(300 * NativeWidget.Dpi(hwnd) / 96.0);
            var width = Math.Max(minWidth, windowOrigin.Width + ((resizeEdges & 1) != 0 ? -dx : (resizeEdges & 2) != 0 ? dx : 0));
            var height = Math.Max(minHeight, windowOrigin.Height + ((resizeEdges & 4) != 0 ? -dy : (resizeEdges & 8) != 0 ? dy : 0));
            AppWindow.MoveAndResize(new RectInt32(windowOrigin.X + ((resizeEdges & 1) != 0 ? windowOrigin.Width - width : 0),
                windowOrigin.Y + ((resizeEdges & 4) != 0 ? windowOrigin.Height - height : 0), width, height));
        }
        args.Handled = true;
    }

    private void EndDrag(object sender, PointerRoutedEventArgs args)
    {
        if (!dragging && !resizing) return;
        dragging = resizing = false; Root.ReleasePointerCapture(args.Pointer);
        UpdateResizeCursor(locked ? 0 : EdgesAt(args.GetCurrentPoint(Root).Position));
        SavePlacement(); args.Handled = true;
    }

    private int EdgesAt(Point point) =>
        (point.X < 10 ? 1 : point.X > Root.ActualWidth - 10 ? 2 : 0)
        | (point.Y < 10 ? 4 : point.Y > Root.ActualHeight - 10 ? 8 : 0);

    private int NativeCursorEdges()
    {
        if (locked || dragging || windowClosed) return 0;
        if (resizing) return resizeEdges;
        var point = NativeWidget.Cursor();
        var rect = WindowRect;
        var scale = NativeWidget.Dpi(hwnd) / 96.0;
        return EdgesAt(new Point((point.X - rect.X) / scale, (point.Y - rect.Y) / scale));
    }

    private void UpdateResizeCursor(int edges) => Root.SetPointerShape(edges switch
    {
        1 or 2 => InputSystemCursorShape.SizeWestEast,
        4 or 8 => InputSystemCursorShape.SizeNorthSouth,
        5 or 10 => InputSystemCursorShape.SizeNorthwestSoutheast,
        6 or 9 => InputSystemCursorShape.SizeNortheastSouthwest,
        _ => InputSystemCursorShape.Arrow
    });

    private async void WindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (approvedClose) return;
        args.Cancel = true;
        if (closing) return;
        closing = true;
        try
        {
            if (editor != null && !await editor.RequestCloseAsync()) return;
            SavePlacement(); timer.Stop(); native.Dispose(); testWindow?.Close();
            approvedClose = true; Close();
        }
        finally { closing = false; }
    }
}

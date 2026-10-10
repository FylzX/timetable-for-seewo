using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using Windows.UI;

namespace Timetable.Editor;

public sealed partial class MainWindow
{
    private bool templatesExpanded = true;
    private ScrollViewer? exceptionScroll;
    private ScrollViewer? weeklyScroll;
    private string? selectedExceptionKey;
    private double exceptionColumnWidth;
    private bool exceptionResizeQueued;

    private void ExceptionHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (data.Exceptions.Count == 0 || args.NewSize.Width < 100) return;
        var desired = Math.Max(180, (args.NewSize.Width - 64) / 5);
        if (Math.Abs(desired - exceptionColumnWidth) < 2) return;
        exceptionColumnWidth = desired;
        if (exceptionResizeQueued) return;
        exceptionResizeQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            exceptionResizeQueued = false;
            if (data.Exceptions.Count > 0) RenderExceptions();
        });
    }

    private Button FlatButton(object content, RoutedEventHandler? clicked = null)
    {
        var button = new Button { Content = content, MinHeight = 0, MinWidth = 0,
            Style = (Style)Root.Resources["CardActionStyle"], HorizontalAlignment = HorizontalAlignment.Stretch };
        if (clicked != null) button.Click += clicked;
        return button;
    }

    private void ToggleTemplatesClicked(object sender, RoutedEventArgs args)
    {
        templatesExpanded = !templatesExpanded;
        WeeklyDrawerColumn.Width = ExceptionDrawerColumn.Width = new GridLength(templatesExpanded ? 220 : 48);
        RenderTemplates();
    }

    private void RenderTemplates()
    {
        WeeklyTemplateHost.Child = templatesExpanded ? TemplatePanel(false) : TemplateRail();
        ExceptionTemplateHost.Child = templatesExpanded ? TemplatePanel(true) : TemplateRail();
    }

    private Button TemplateRail()
    {
        var label = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Top };
        label.Children.Add(new FontIcon { Glyph = "\uE76C", FontSize = 12 });
        label.Children.Add(Text("时\n间\n模\n板", 13));
        var button = FlatButton(label, ToggleTemplatesClicked);
        button.VerticalAlignment = VerticalAlignment.Stretch;
        button.VerticalContentAlignment = VerticalAlignment.Top;
        button.Padding = new Thickness(8, 16, 8, 0);
        ToolTipService.SetToolTip(button, "向右展开时间模板");
        AutomationProperties.SetName(button, "展开时间模板");
        return button;
    }

    private Grid TemplatePanel(bool exceptions)
    {
        var grid = new Grid { Padding = new Thickness(12), RowSpacing = 8 };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new Grid { Height = 36 };
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = Text("共享时间模板", 15, true); title.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(title, "编辑后按需应用，不会自动改变已有课表。"); heading.Children.Add(title);
        var collapse = FlatButton(new FontIcon { Glyph = "\uE76B", FontSize = 12 }, ToggleTemplatesClicked);
        collapse.Width = 24; Grid.SetColumn(collapse, 1); heading.Children.Add(collapse);
        ToolTipService.SetToolTip(collapse, "收起到左侧"); AutomationProperties.SetName(collapse, "收起时间模板");
        grid.Children.Add(heading);
        var times = new StackPanel { Spacing = 2 };
        foreach (var row in data.Template)
        {
            var line = new Grid { Height = 38, ColumnSpacing = 4, Padding = new Thickness(6, 0, 0, 0) };
            line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var time = FlatButton(Text(row.TimeLabel, 13), async (_, _) =>
            {
                var edited = await EditTime(row, data.Template);
                if (edited != null) { row.Start = edited.Start; row.End = edited.End; Sort(data.Template); RenderTemplates(); Changed(); }
            });
            ToolTipService.SetToolTip(time, "修改时间段"); line.Children.Add(time);
            var remove = DeleteButton("删除时间段", (_, _) => { data.Template.Remove(row); RenderTemplates(); Changed(); });
            Grid.SetColumn(remove, 1); line.Children.Add(remove);
            RevealOnInteraction(line, remove);
            times.Children.Add(line);
        }
        if (data.Template.Count == 0) times.Children.Add(Text("还没有时间段", 13));
        var scroll = new ScrollViewer { Content = times, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var actions = new StackPanel { Spacing = 10 };
        var add = FlatButton(Text("＋ 添加时间段", 13), async (_, _) =>
        {
            var row = await EditTime(null, data.Template);
            if (row != null) { data.Template.Add(row); Sort(data.Template); RenderTemplates(); Changed(); }
        });
        add.Height = 30; actions.Children.Add(add);
        var target = new ComboBox { Header = "应用范围", HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = exceptions ? "请先添加例外日期" : "选择工作日" };
        var keys = new List<string>();
        if (!exceptions) { keys.Add("all"); target.Items.Add("所有工作日"); }
        foreach (var key in exceptions ? data.Exceptions.Keys : Enumerable.Range(1, 5).Select(day => day.ToString()))
        { keys.Add(key); target.Items.Add(exceptions ? DateLabel(key) + " · " + key[..4] : WeekdayName(key)); }
        if (keys.Count > 0) target.SelectedIndex = 0;
        actions.Children.Add(target);
        var apply = ActionButton("应用模板", async (_, _) =>
        {
            if (target.SelectedIndex < 0) return;
            var key = keys[target.SelectedIndex];
            await ApplyTemplate(exceptions, key == "all" ? Enumerable.Range(1, 5).Select(day => day.ToString()).ToArray() : [key]);
        }, true);
        apply.IsEnabled = keys.Count > 0; actions.Children.Add(apply);
        Grid.SetRow(actions, 2); grid.Children.Add(actions);
        return grid;
    }

    private static string DateLabel(string key)
    {
        var day = DateOnly.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"{day.Month}月{day.Day}日";
    }

    private void RenderWeek()
    {
        var keys = Enumerable.Range(1, 5).Select(day => day.ToString()).ToArray();
        var board = BuildBoard(keys, false);
        board.MinHeight = NaturalBoardHeight(new TimelineLayout(keys.SelectMany(key => data.Weekly[key])));
        if (weeklyScroll == null)
        {
            weeklyScroll = new ScrollViewer { VerticalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, ZoomMode = ZoomMode.Disabled,
                VerticalContentAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            WeeklyBoardHost.Children.Add(ScrollFrame(weeklyScroll, "主课表"));
        }
        ReplaceBoardContent(weeklyScroll, board);
    }

    private void RenderExceptions()
    {
        if (data.Exceptions.Count == 0)
        {
            ExceptionBoardHost.Children.Clear(); exceptionScroll = null;
            var empty = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new FontIcon { Glyph = "\uE787", FontSize = 32 });
            empty.Children.Add(Text("还没有例外日期", 20, true));
            var hint = Text("添加日期，为调课或假期创建独立安排。", 13);
            hint.Foreground = Brush("TextFillColorSecondaryBrush"); empty.Children.Add(hint);
            ExceptionBoardHost.Children.Add(empty); return;
        }
        var keys = data.Exceptions.Keys.ToArray();
        if (selectedExceptionKey == null || !data.Exceptions.ContainsKey(selectedExceptionKey))
            selectedExceptionKey = keys[0];
        if (exceptionColumnWidth <= 0) exceptionColumnWidth = 220;
        var board = BuildBoard(keys, true, exceptionColumnWidth);
        if (exceptionScroll == null)
        {
            ExceptionBoardHost.Children.Clear();
            exceptionScroll = new ScrollViewer { HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible, ZoomMode = ZoomMode.Disabled,
                VerticalContentAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            ExceptionBoardHost.Children.Add(ScrollFrame(exceptionScroll, "例外课表"));
        }
        // Exception columns use the same five-column width as the main board. With more
        // than five dates the scroll viewer exposes the remaining dates horizontally.
        void FitWidth()
        {
            var slot = exceptionColumnWidth;
            board.Width = 64 + Math.Max(5, keys.Length) * slot;
            // The content height comes from the actual timeline. It only exceeds the
            // viewport when the schedule needs more room for readable cards.
            board.MinHeight = NaturalBoardHeight(new TimelineLayout(keys.SelectMany(key => data.Exceptions[key])));
        }
        FitWidth();
        ReplaceBoardContent(exceptionScroll, board);
    }

    private void ReplaceBoardContent(ScrollViewer scroll, FrameworkElement board)
    {
        var horizontal = scroll.HorizontalOffset;
        var vertical = scroll.VerticalOffset;
        // Replacing a focused delete button can make WinUI focus the first card
        // and bring it into view. Keep focus on the existing scroll container.
        if (Root.XamlRoot != null)
            for (var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; focused != null; focused = VisualTreeHelper.GetParent(focused))
                if (focused == scroll) { scroll.Focus(FocusState.Programmatic); break; }
        void Restore()
        {
            if (!ReferenceEquals(scroll.Content, board) || !board.IsLoaded) return;
            scroll.UpdateLayout();
            scroll.ChangeView(Math.Min(horizontal, scroll.ScrollableWidth), Math.Min(vertical, scroll.ScrollableHeight), null, true);
        }
        board.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Restore);
        scroll.Content = board;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Restore);
    }

    private static double NaturalBoardHeight(TimelineLayout timeline) => timeline.MinimumExtent + 92;

    private Grid ScrollFrame(ScrollViewer scroll, string label)
    {
        var scrollFrame = new Grid();
        scrollFrame.RowDefinitions.Add(new() { Height = GridLength.Auto });
        scrollFrame.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        scrollFrame.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var up = FlatButton(new TextBlock { Text = "▲", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
            (_, _) => scroll.ChangeView(null, Math.Max(0, scroll.VerticalOffset - scroll.ViewportHeight * .8), null));
        up.Width = 34; up.Height = 26; up.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTipService.SetToolTip(up, "向上滚动"); AutomationProperties.SetName(up, label + "向上滚动");
        scrollFrame.Children.Add(up);
        Grid.SetRow(scroll, 1); scrollFrame.Children.Add(scroll);
        var down = FlatButton(new TextBlock { Text = "▼", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
            (_, _) => scroll.ChangeView(null, Math.Min(scroll.ScrollableHeight, scroll.VerticalOffset + scroll.ViewportHeight * .8), null));
        down.Width = 34; down.Height = 26; down.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTipService.SetToolTip(down, "向下滚动"); AutomationProperties.SetName(down, label + "向下滚动");
        Grid.SetRow(down, 2); scrollFrame.Children.Add(down);
        return scrollFrame;
    }

    private Grid BuildBoard(string[] keys, bool exceptions, double fixedColumnWidth = 0)
    {
        var source = exceptions ? data.Exceptions : data.Weekly;
        var timeline = new TimelineLayout(keys.SelectMany(key => source[key]));
        var board = new Grid();
        board.RowDefinitions.Add(new() { Height = new GridLength(44) });
        board.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        board.RowDefinitions.Add(new() { Height = new GridLength(32) });
        var headings = new Grid(); var footer = new Grid();
        var emptyColumns = new Dictionary<int, StackPanel>();
        foreach (var grid in new[] { headings, footer })
        {
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(64) });
            foreach (var _ in keys) grid.ColumnDefinitions.Add(new()
            { Width = exceptions && fixedColumnWidth > 0 ? new GridLength(fixedColumnWidth) : new GridLength(1, GridUnitType.Star) });
        }
        var axisHeading = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var axisTitle = Text("时间", 11); axisTitle.Foreground = Brush("TextFillColorTertiaryBrush"); axisHeading.Children.Add(axisTitle);
        var axisHint = Text("点击卡片修改", 9); axisHint.Foreground = Brush("TextFillColorTertiaryBrush"); axisHeading.Children.Add(axisHint);
        ToolTipService.SetToolTip(axisHeading, "课程卡片可直接点击修改；点击时间可调整时间段。\n所有列共享时间轴，长空档已压缩。");
        headings.Children.Add(axisHeading);
        for (var index = 0; index < keys.Length; index++)
        {
            var key = keys[index]; var rows = source[key];
            var title = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(Text(exceptions ? DateLabel(key) : WeekdayName(key), 17, true));
            var caption = Text(exceptions
                ? key[..4] + " · " + Weekdays[((int)DateOnly.Parse(key).DayOfWeek + 6) % 7]
                    + (key == selectedExceptionKey ? " · 已选中" : "")
                : $"{rows.Count} 节课程", 11);
            caption.Foreground = Brush("TextFillColorSecondaryBrush"); title.Children.Add(caption);
            var heading = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 0, 8, 0) };
            heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); heading.Children.Add(title);
            if (exceptions)
            {
                heading.PointerPressed += (_, _) =>
                {
                    selectedExceptionKey = key;
                    RenderExceptions();
                };
                var removeDate = DeleteButton("删除整个日期", async (_, _) =>
                {
                    if (await Confirm("删除例外日期？", $"删除 {key} 的全部例外课程？保存后该日期恢复使用主课表。", "删除日期"))
                    { data.Exceptions.Remove(key); RenderExceptions(); RenderTemplates(); Changed(); }
                });
                Grid.SetColumn(removeDate, 1); heading.Children.Add(removeDate); RevealOnInteraction(heading, removeDate);
            }
            Grid.SetColumn(heading, index + 1); headings.Children.Add(heading);
            var add = FlatButton(Text("＋ 添加课程", 12), async (_, _) =>
            {
                var row = await EditTime(null, rows);
                if (row != null) { rows.Add(row); Sort(rows); Rebuild(); Changed(); }
            });
            var targetName = exceptions ? key : WeekdayName(key);
            ToolTipService.SetToolTip(add, "向 " + targetName + " 添加课程");
            AutomationProperties.SetName(add, "向 " + targetName + " 添加课程");
            if (rows.Count == 0)
            {
                add.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                add.Content = "＋ 添加课程";
                add.FontSize = 14;
                add.Height = 36;
                add.HorizontalContentAlignment = HorizontalAlignment.Center;
                var empty = Text(exceptions ? "当天放假" : "暂无课程", 13);
                empty.Foreground = Brush("TextFillColorSecondaryBrush");
                empty.TextAlignment = TextAlignment.Center;
                var panel = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
                panel.Children.Add(empty); panel.Children.Add(add);
                emptyColumns[index] = panel;
            }
            else
            {
                add.Foreground = Brush("TextFillColorSecondaryBrush"); add.Margin = new Thickness(10, 0, 8, 0);
                Grid.SetColumn(add, index + 1); footer.Children.Add(add);
            }
        }
        board.Children.Add(headings); Grid.SetRow(footer, 2); board.Children.Add(footer);
        var surface = new TimelinePanel(timeline, keys.Length, exceptions, fixedColumnWidth);
        Grid.SetRow(surface, 1); board.Children.Add(surface);
        if (exceptions)
            for (var index = 0; index < keys.Length - 1; index++)
                surface.AddColumnDivider(index, Brush("DividerStrokeColorDefaultBrush"));
        void Rebuild() { if (exceptions) RenderExceptions(); else RenderWeek(); }
        for (var index = 0; index < timeline.Minutes.Count; index++)
        {
            var minute = timeline.Minutes[index];
            var begin = keys.SelectMany(key => source[key]).Any(row => TimelineLayout.Minute(row.Start) == minute);
            // Show meaningful boundaries, avoiding a mesh of lines from tiny offsets between days.
            if (begin || index == timeline.Minutes.Count - 1)
            {
                surface.AddLine(new Border { Background = Brush("DividerStrokeColorDefaultBrush"), Opacity = .25, IsHitTestVisible = false }, minute);
                var tick = Text($"{minute / 60:00}:{minute % 60:00}", 11);
                tick.Foreground = Brush("TextFillColorTertiaryBrush"); tick.IsHitTestVisible = false;
                surface.AddTick(tick, minute);
            }
        }
        for (var index = 0; index < keys.Length; index++)
        {
            var rows = source[keys[index]];
            if (rows.Count == 0)
            {
                surface.AddEmpty(emptyColumns[index], index);
            }
            foreach (var row in rows)
            {
                var card = CourseCard(row, rows, Rebuild, out var fit);
                surface.AddLesson(card, index, row.Start, row.End, fit);
            }
        }
        return board;
    }

    private Button DeleteButton(string description, RoutedEventHandler clicked)
    {
        var icon = new FontIcon { Glyph = "\uE74D", FontSize = 15, Foreground = new SolidColorBrush(Color.FromArgb(255, 196, 55, 65)) };
        var button = new Button { Content = icon, Width = 28, Height = double.NaN,
            MinWidth = 0, MinHeight = 0, Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Stretch,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
        button.Foreground = icon.Foreground;
        ToolTipService.SetToolTip(button, description); AutomationProperties.SetName(button, description);
        button.Click += clicked; return button;
    }

    private static void RevealOnInteraction(FrameworkElement container, params FrameworkElement[] controls)
    {
        bool hovered = false;
        bool FocusInside()
        {
            var current = container.XamlRoot == null ? null : FocusManager.GetFocusedElement(container.XamlRoot) as DependencyObject;
            while (current != null) { if (current == container) return true; current = VisualTreeHelper.GetParent(current); }
            return false;
        }
        void Update()
        {
            var visible = hovered || FocusInside();
            foreach (var control in controls) { control.Opacity = visible ? 1 : 0; control.IsHitTestVisible = visible; }
        }
        foreach (var control in controls) { control.Opacity = 0; control.IsHitTestVisible = false; }
        container.PointerEntered += (_, _) => { hovered = true; Update(); };
        container.PointerExited += (_, _) => { hovered = false; Update(); };
        container.GotFocus += (_, _) => Update();
        container.LostFocus += (_, _) => container.DispatcherQueue.TryEnqueue(Update);
    }

    private Border CourseCard(Lesson row, List<Lesson> rows, Action rebuild, out Action<double> fit)
    {
        var card = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 6, 4) };
        var body = new Grid { RowSpacing = 0 };
        body.RowDefinitions.Add(new() { Height = new GridLength(14) });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(20) });
        var timeLabel = Text(row.TimeLabel, 10); timeLabel.TextWrapping = TextWrapping.NoWrap;
        timeLabel.LineHeight = 14; timeLabel.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        timeLabel.TextTrimming = TextTrimming.CharacterEllipsis; timeLabel.Foreground = Brush("TextFillColorSecondaryBrush");
        var time = FlatButton(timeLabel, async (_, _) =>
        {
            var edited = await EditTime(row, rows);
            if (edited != null) { row.Start = edited.Start; row.End = edited.End; Sort(rows); rebuild(); Changed(); }
        });
        ToolTipService.SetToolTip(time, "修改时间 · " + row.TimeLabel); body.Children.Add(time);
        var courseLabel = Text("", 16, true); courseLabel.TextWrapping = TextWrapping.NoWrap;
        courseLabel.LineHeight = 22; courseLabel.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        courseLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        var course = FlatButton(courseLabel); Grid.SetRow(course, 1); body.Children.Add(course);
        var remove = DeleteButton("删除课程", (_, _) => { rows.Remove(row); rebuild(); Changed(); });
        remove.Width = 28; remove.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetColumn(remove, 1); Grid.SetRowSpan(remove, 2); body.Children.Add(remove);
        // Course selection is available by clicking the label or anywhere on the card;
        // there is no permanent arrow competing with the delete action.
        card.Child = body; RevealOnInteraction(card, remove);
        void UpdateCourse()
        {
            var blank = string.IsNullOrWhiteSpace(row.Course);
            courseLabel.Text = blank ? "选择课程" : row.Course;
            courseLabel.Foreground = Brush(blank ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush");
            ToolTipService.SetToolTip(course, blank ? "选择或输入课程名称" : row.Course + " · 点击修改");
            AutomationProperties.SetName(course, blank ? "选择课程" : "修改课程 " + row.Course);
            card.Background = CourseBrush(row.Course);
        }
        UpdateCourse();
        bool layoutConfigured = false;
        fit = height =>
        {
            if (layoutConfigured) return;
            layoutConfigured = true;
            // Every card uses the same two-row layout: time at the upper left and
            // course name beneath it. This keeps time positions aligned visually.
            body.RowDefinitions[0].Height = new GridLength(14);
            body.RowDefinitions[1].Height = GridLength.Auto;
            body.ColumnDefinitions[1].Width = new GridLength(28);
            Grid.SetRow(course, 1); Grid.SetColumn(course, 0);
            Grid.SetRow(time, 0); Grid.SetColumn(time, 0);
            Grid.SetColumn(remove, 1); Grid.SetRow(remove, 0); Grid.SetRowSpan(remove, 2);
            remove.HorizontalAlignment = HorizontalAlignment.Stretch;
            remove.VerticalAlignment = VerticalAlignment.Stretch;
            remove.Margin = new Thickness(0);
            time.Margin = new Thickness(0);
            courseLabel.FontSize = 16;
            timeLabel.FontSize = 10;
            card.Padding = new Thickness(8, 4, 0, 4);
        };
        var menu = new MenuFlyout();
        foreach (var name in Courses)
        {
            var item = new MenuFlyoutItem { Text = name };
            item.Click += (_, _) => { row.Course = name; UpdateCourse(); Changed(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var custom = new MenuFlyoutItem { Text = "自定义课程…", Icon = new SymbolIcon(Symbol.Edit) };
        custom.Click += async (_, _) =>
        {
            var field = new TextBox { Header = "课程名称", PlaceholderText = "例如：自习、班会、数学周测", Text = row.Course, MaxLength = 80 };
            var error = Text("", 12); error.Foreground = Brush("SystemFillColorCriticalBrush");
            var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(field); panel.Children.Add(error);
            var dialog = new ContentDialog { Title = "自定义课程", Content = panel, PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            dialog.Opened += (_, _) => { field.Focus(FocusState.Programmatic); field.SelectAll(); };
            dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(field.Text)) { args.Cancel = true; error.Text = "请输入课程名称。"; } };
            if (await Dialog(dialog) == ContentDialogResult.Primary) { row.Course = field.Text.Trim(); UpdateCourse(); Changed(); }
        };
        menu.Items.Add(custom);
        course.Flyout = menu;
        // The whole card is an edit target. Keep the time button reserved for time editing.
        card.PointerPressed += (_, args) =>
        {
            var source = args.OriginalSource as DependencyObject;
            var insideTime = false;
            while (source != null && source != card)
            {
                if (source == time || source == remove || source == course) { insideTime = true; break; }
                source = VisualTreeHelper.GetParent(source);
            }
            if (!insideTime && source != time && source != remove) menu.ShowAt(card);
        };
        return card;
    }

    private Brush CourseBrush(string course)
    {
        if (string.IsNullOrWhiteSpace(course)) return Brush("LayerFillColorDefaultBrush");
        var group = course.Contains("语文") ? 0 : course.Contains("数学") ? 1 : course.Contains("英语") ? 2 :
            course.Contains("体育") || course.Contains("操") ? 3 : course.Contains("物理") || course.Contains("化学") ? 4 :
            course.Contains("等级") ? 5 : 6;
        Color[] light = [Color.FromArgb(255, 234, 239, 251), Color.FromArgb(255, 229, 243, 239), Color.FromArgb(255, 241, 233, 250),
            Color.FromArgb(255, 247, 239, 222), Color.FromArgb(255, 228, 242, 249), Color.FromArgb(255, 246, 234, 236), Color.FromArgb(255, 233, 237, 242)];
        Color[] dark = [Color.FromArgb(255, 39, 48, 67), Color.FromArgb(255, 34, 56, 50), Color.FromArgb(255, 51, 41, 63),
            Color.FromArgb(255, 59, 51, 36), Color.FromArgb(255, 33, 52, 62), Color.FromArgb(255, 61, 41, 45), Color.FromArgb(255, 44, 48, 55)];
        return new SolidColorBrush((Root.ActualTheme == ElementTheme.Dark ? dark : light)[group]);
    }
}

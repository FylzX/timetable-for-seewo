using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Timetable.Desktop;

internal sealed class TestTimeWindow : Window
{
    private readonly SimulationClock clock;
    private readonly Action changed;
    private readonly DatePicker datePicker = new() { Header = "日期", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TimePicker timePicker = new() { Header = "时间", ClockIdentifier = "24HourClock", MinuteIncrement = 1 };
    private readonly NumberBox seconds = new() { Header = "秒", Minimum = 0, Maximum = 59, Value = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ClockDial dial = new();
    private readonly TextBlock status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool syncing, edited;

    internal TestTimeWindow(SimulationClock clock, Action changed)
    {
        this.clock = clock; this.changed = changed;
        Title = "调整时间(测试用)";
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "调整时间(测试用)", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "拖动时针或分针，也可以直接选择日期和时间。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(dial);
        var hint = new TextBlock { Text = "24 小时制 · 拨动时针保留当前上午 / 下午", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
        hint.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]; panel.Children.Add(hint);
        panel.Children.Add(datePicker);
        var fields = new Grid { ColumnSpacing = 12 };
        fields.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new() { Width = new GridLength(90) });
        timePicker.HorizontalAlignment = HorizontalAlignment.Stretch;
        fields.Children.Add(timePicker); Grid.SetColumn(seconds, 1); fields.Children.Add(seconds); panel.Children.Add(fields);
        panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var use = new Button { Content = "启用测试时间", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        use.Click += (_, _) =>
        {
            clock.Set(Selected()); edited = false; changed(); Refresh();
        };
        var reset = new Button { Content = "恢复电脑时间" };
        reset.Click += (_, _) => { clock.Reset(); edited = false; changed(); Refresh(); };
        buttons.Children.Add(reset); buttons.Children.Add(use); panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = "只影响课表。关闭窗口后测试时间继续走，重启程序恢复电脑时间。", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        datePicker.DateChanged += (_, _) => { if (!syncing) edited = true; };
        timePicker.TimeChanged += (_, _) => FieldChanged();
        seconds.ValueChanged += (_, _) => FieldChanged();
        dial.TimeChanged += value => { syncing = true; timePicker.Time = value; syncing = false; edited = true; };
        timer.Tick += (_, _) => Refresh(); timer.Start();
        Closed += (_, _) => timer.Stop();
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = NativeWidget.Dpi(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd), DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(520 * dpi), area.Width - 32);
        var height = Math.Min((int)(690 * dpi), area.Height - 32);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsMaximizable = false;
        Refresh();
    }

    private void FieldChanged()
    {
        if (syncing) return;
        edited = true;
        dial.Time = timePicker.Time;
    }

    private void Refresh()
    {
        status.Text = clock.Active ? $"测试时间正在继续走 · {clock.Now:yyyy-MM-dd HH:mm:ss}" : $"同步电脑时间 · {clock.Now:yyyy-MM-dd HH:mm:ss}";
        if (edited || dial.Dragging) return;
        syncing = true;
        var now = clock.Now;
        datePicker.Date = new DateTimeOffset(now);
        timePicker.Time = new TimeSpan(now.Hour, now.Minute, 0);
        seconds.Value = now.Second;
        dial.Time = now.TimeOfDay;
        syncing = false;
    }

    private DateTime Selected()
    {
        var date = datePicker.Date;
        var time = timePicker.Time;
        var second = double.IsFinite(seconds.Value) ? (int)Math.Clamp(seconds.Value, 0, 59) : 0;
        return new DateTime(date.Year, date.Month, date.Day, time.Hours, time.Minutes, second, DateTimeKind.Local);
    }
}

internal sealed class ClockDial : Canvas
{
    private readonly Line hour = new() { StrokeThickness = 6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Line minute = new() { StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private TimeSpan time;
    private bool hourDragging;
    public bool Dragging { get; private set; }
    public event Action<TimeSpan>? TimeChanged;
    public TimeSpan Time { get => time; set { time = value; RenderHands(); } }

    public ClockDial()
    {
        Width = Height = 220; HorizontalAlignment = HorizontalAlignment.Center;
        Background = new SolidColorBrush(Colors.Transparent);
        var outline = new Ellipse { Width = 216, Height = 216, StrokeThickness = 2,
            Stroke = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], Fill = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"] };
        SetLeft(outline, 2); SetTop(outline, 2); Children.Add(outline);
        for (var tick = 0; tick < 60; tick++)
        {
            var angle = tick * Math.PI / 30;
            var length = tick % 5 == 0 ? 10 : 4;
            Children.Add(new Line { X1 = 110 + Math.Sin(angle) * (100 - length), Y1 = 110 - Math.Cos(angle) * (100 - length),
                X2 = 110 + Math.Sin(angle) * 100, Y2 = 110 - Math.Cos(angle) * 100,
                Stroke = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], StrokeThickness = tick % 5 == 0 ? 2 : 1 });
        }
        hour.Stroke = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        minute.Stroke = new SolidColorBrush(Color.FromArgb(255, 55, 111, 229));
        Children.Add(hour); Children.Add(minute);
        var pivot = new Ellipse { Width = 12, Height = 12, Fill = minute.Stroke };
        SetLeft(pivot, 104); SetTop(pivot, 104); Children.Add(pivot);
        PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed) return;
            hourDragging = Distance(point.Position, hour) < Distance(point.Position, minute);
            Dragging = true; CapturePointer(args.Pointer); Move(point.Position); args.Handled = true;
        };
        PointerMoved += (_, args) => { if (Dragging) { Move(args.GetCurrentPoint(this).Position); args.Handled = true; } };
        PointerReleased += (_, args) => { Dragging = false; ReleasePointerCapture(args.Pointer); args.Handled = true; };
        PointerCaptureLost += (_, _) => Dragging = false;
        RenderHands();
    }

    private static double Distance(Point point, Line line)
    {
        var dx = line.X2 - line.X1; var dy = line.Y2 - line.Y1;
        var t = Math.Clamp(((point.X - line.X1) * dx + (point.Y - line.Y1) * dy) / (dx * dx + dy * dy), 0, 1);
        return Math.Pow(point.X - line.X1 - t * dx, 2) + Math.Pow(point.Y - line.Y1 - t * dy, 2);
    }
    private void Move(Point point)
    {
        var angle = (Math.Atan2(point.X - 110, 110 - point.Y) + 2 * Math.PI) % (2 * Math.PI);
        Time = hourDragging
            ? new TimeSpan(((int)Math.Round(angle * 6 / Math.PI) % 12) + (time.Hours >= 12 ? 12 : 0), time.Minutes, time.Seconds)
            : new TimeSpan(time.Hours, (int)Math.Round(angle * 30 / Math.PI) % 60, time.Seconds);
        TimeChanged?.Invoke(time);
    }
    private void RenderHands()
    {
        void Set(Line line, double angle, double length)
        { line.X1 = line.Y1 = 110; line.X2 = 110 + Math.Sin(angle) * length; line.Y2 = 110 - Math.Cos(angle) * length; }
        Set(hour, (time.Hours % 12 + time.Minutes / 60.0) * Math.PI / 6, 56);
        Set(minute, time.Minutes * Math.PI / 30, 83);
    }
}

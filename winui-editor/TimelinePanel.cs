using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Timetable.Editor;

/// <summary>
/// Positions existing controls during XAML measure/arrange, including the first layout
/// and tab activation. No size-event redraw may erase a board that is still being measured.
/// </summary>
internal sealed class TimelinePanel(TimelineLayout timeline, int columns, bool exceptions, double fixedColumnWidth = 0) : Panel
{
    private enum Kind { Lesson, Line, Tick, Empty, ColumnDivider }
    private sealed record Item(UIElement Element, Kind Kind, int Column, int Start, int End, Action<double>? Fit = null);
    private readonly List<Item> items = [];
    private const double AxisWidth = 64;
    private const double Inset = 8;

    private void Add(Item item) { items.Add(item); Children.Add(item.Element); }
    public void AddLesson(UIElement element, int column, string start, string end, Action<double> fit) =>
        Add(new(element, Kind.Lesson, column, TimelineLayout.Minute(start), TimelineLayout.Minute(end), fit));
    public void AddLine(UIElement element, int minute) => Add(new(element, Kind.Line, 0, minute, minute));
    public void AddTick(UIElement element, int minute) => Add(new(element, Kind.Tick, 0, minute, minute));
    public void AddEmpty(UIElement element, int column) => Add(new(element, Kind.Empty, column, 0, 0));
    public void AddColumnDivider(int column, Brush brush)
    {
        Add(new(new Border { Width = 1, Background = brush, Opacity = .65, IsHitTestVisible = false },
            Kind.ColumnDivider, column, 0, 0));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Supply a meaningful natural size when a parent measures without a constraint.
        // The star-sized board normally supplies the available viewport in both dimensions.
        var size = new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : AxisWidth + columns * 210,
            double.IsFinite(availableSize.Height) ? availableSize.Height : timeline.MinimumExtent + 2 * Inset);
        foreach (var item in items)
        {
            var rect = Bounds(item, size);
            item.Fit?.Invoke(rect.Height);
            item.Element.Measure(new Size(rect.Width, rect.Height));
        }
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var lastTick = -100.0;
        var lastY = Inset + Math.Max(0, finalSize.Height - 2 * Inset);
        foreach (var item in items)
        {
            var rect = Bounds(item, finalSize);
            if (item.Kind == Kind.Tick)
            {
                var y = rect.Y + 8;
                var isLast = item.Start == timeline.Minutes[^1];
                var visible = isLast || (y - lastTick >= 24 && lastY - y >= 24);
                item.Element.Opacity = visible ? 1 : 0;
                if (visible) lastTick = y;
            }
            item.Element.Arrange(rect);
        }
        return finalSize;
    }

    private Rect Bounds(Item item, Size size)
    {
        var extent = Math.Max(0, size.Height - 2 * Inset);
        var columnWidth = fixedColumnWidth > 0 ? fixedColumnWidth : Math.Max(0, size.Width - AxisWidth) / columns;
        var y = Inset + timeline.Position(item.Start, extent);
        return item.Kind switch
        {
            Kind.Lesson => new Rect(AxisWidth + item.Column * columnWidth + (exceptions ? 12 : 6), y + 1,
                Math.Max(0, columnWidth - (exceptions ? 28 : 12)),
                Math.Max(0, timeline.Position(item.End, extent) - timeline.Position(item.Start, extent) - 4)),
            Kind.Line => new Rect(AxisWidth, y, Math.Max(0, size.Width - AxisWidth), 1),
            Kind.Tick => new Rect(0, Math.Max(0, y - 8), AxisWidth - 6, 16),
            Kind.ColumnDivider => new Rect(AxisWidth + (item.Column + 1) * columnWidth - 4, 0, 1, size.Height),
            _ => new Rect(AxisWidth + item.Column * columnWidth + 10, Math.Max(0, (size.Height - 76) / 2),
                Math.Max(0, columnWidth - 20), Math.Min(76, size.Height))
        };
    }
}

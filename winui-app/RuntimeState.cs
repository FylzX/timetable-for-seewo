using System.Diagnostics;
using System.Globalization;
using Timetable.Editor;

namespace Timetable.Desktop;

internal sealed class SimulationClock
{
    private readonly Stopwatch elapsed = new();
    private DateTime anchor;
    public bool Active => elapsed.IsRunning;
    public DateTime Now => Active ? anchor + elapsed.Elapsed : DateTime.Now;
    public void Set(DateTime value) { anchor = value; elapsed.Restart(); }
    public void Reset() => elapsed.Reset();
}

internal static class DailySchedule
{
    public static List<Lesson> Rows(EditorData data, DateOnly date) =>
        data.Exceptions.TryGetValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var rows)
            ? rows : data.Weekly.GetValueOrDefault((((int)date.DayOfWeek + 6) % 7 + 1).ToString(), []);

    // End times are exclusive. Gaps and the period before school point to the next class.
    public static (int Index, bool Live)? Marker(IReadOnlyList<Lesson> rows, DateOnly shownDate, DateTime now)
    {
        if (shownDate != DateOnly.FromDateTime(now)) return null;
        var time = TimeOnly.FromDateTime(now);
        for (var index = 0; index < rows.Count; index++)
        {
            var start = TimeOnly.ParseExact(rows[index].Start, "HH:mm", CultureInfo.InvariantCulture);
            var end = TimeOnly.ParseExact(rows[index].End, "HH:mm", CultureInfo.InvariantCulture);
            if (start <= time && time < end) return (index, true);
            if (time < start) return (index, false);
        }
        return null;
    }
}

internal sealed record Placement(double Width = .36, double Height = .60, double X = .965, double Y = .18, bool Locked = false,
    int MonitorX = 0, int MonitorY = 0)
{
    public (int X, int Y, int Width, int Height) Restore(int left, int top, int width, int height)
    {
        var w = Math.Clamp((int)Math.Round(height * Finite(Width, .36, .20, 1.8)), Math.Min(220, width), width);
        var h = Math.Clamp((int)Math.Round(height * Finite(Height, .60, .30, 1)), Math.Min(260, height), height);
        return (left + (int)Math.Round((width - w) * Finite(X, .965, 0, 1)),
            top + (int)Math.Round((height - h) * Finite(Y, .18, 0, 1)), w, h);
    }

    public static Placement Capture(int x, int y, int width, int height, int left, int top, int workWidth, int workHeight, bool locked) =>
        new((double)width / workHeight, (double)height / workHeight,
            Math.Clamp((double)(x - left) / Math.Max(1, workWidth - width), 0, 1),
            Math.Clamp((double)(y - top) / Math.Max(1, workHeight - height), 0, 1), locked, left, top);

    private static double Finite(double value, double fallback, double low, double high) =>
        double.IsFinite(value) ? Math.Clamp(value, low, high) : fallback;
}

using System.Globalization;

namespace Timetable.Editor;

/// <summary>Shared monotone time axis; compress empty breaks, then fit the whole day.</summary>
public sealed class TimelineLayout
{
    public IReadOnlyList<int> Minutes { get; }
    private readonly double[] positions;
    public double MinimumExtent { get; }

    public TimelineLayout(IEnumerable<Lesson> lessons)
    {
        var ranges = lessons.Select(row => (Start: Minute(row.Start), End: Minute(row.End))).ToArray();
        var times = ranges.SelectMany(row => new[] { row.Start, row.End }).Distinct().Order().ToArray();
        if (times.Length == 0) times = [8 * 60, 17 * 60];
        Minutes = times;
        positions = new double[times.Length];
        var indices = times.Select((minute, index) => (minute, index)).ToDictionary(item => item.minute, item => item.index);
        var edges = ranges.GroupBy(row => indices[row.End]).ToDictionary(group => group.Key,
            group => group.Select(row => indices[row.Start]).ToArray());
        for (var index = 1; index < times.Length; index++)
        {
            var occupied = ranges.Any(row => row.Start < times[index] && row.End > times[index - 1]);
            // Small offsets between workdays should not consume a full course row.
            // Actual lessons still reserve 54 units through the constraints below.
            var spacing = occupied ? Math.Min(4, (times[index] - times[index - 1]) * .65) : 4;
            positions[index] = positions[index - 1] + Math.Max(2, spacing);
            if (edges.TryGetValue(index, out var starts))
                // Reserve enough normalized space for the time label and course name in
                // the same two-row layout on every card.
                foreach (var start in starts) positions[index] = Math.Max(positions[index], positions[start] + 64);
        }
        MinimumExtent = Math.Max(1, positions[^1]);
    }

    public static int Minute(string time)
    {
        var parsed = TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture);
        return parsed.Hour * 60 + parsed.Minute;
    }

    public double Position(int minute, double height)
    {
        if (minute <= Minutes[0]) return 0;
        if (minute >= Minutes[^1]) return height;
        var right = 1;
        while (Minutes[right] < minute) right++;
        var fraction = (double)(minute - Minutes[right - 1]) / (Minutes[right] - Minutes[right - 1]);
        return (positions[right - 1] + fraction * (positions[right] - positions[right - 1])) / MinimumExtent * height;
    }

    public double Position(string time, double height) => Position(Minute(time), height);
}

using System.Globalization;

namespace Timetable.Editor;

internal static class TimeText
{
    internal static bool TryParse(string text, out TimeSpan time)
    {
        time = default;
        if (!TimeOnly.TryParseExact(text.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var value)) return false;
        time = value.ToTimeSpan();
        return true;
    }
}

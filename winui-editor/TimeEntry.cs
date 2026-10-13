using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace Timetable.Editor;

// Keep the typed value authoritative, including unfinished or invalid edits.
// The picker only replaces it when the user explicitly selects a time.
internal sealed class TimeEntry : StackPanel
{
    private readonly TextBox input;
    private readonly TimePicker picker;
    private bool syncing;

    internal TimeEntry(string label, TimeSpan value)
    {
        Spacing = 6;
        Children.Add(new TextBlock { Text = label });
        var fields = new Grid { ColumnSpacing = 10 };
        fields.ColumnDefinitions.Add(new() { Width = new GridLength(100) });
        fields.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        input = new TextBox { Text = value.ToString("hh\\:mm", CultureInfo.InvariantCulture),
            PlaceholderText = "HH:mm", MaxLength = 5, VerticalAlignment = VerticalAlignment.Center };
        picker = new TimePicker { ClockIdentifier = "24HourClock", Time = value, MinuteIncrement = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        ToolTipService.SetToolTip(input, "直接输入时间，例如 08:30");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, label + " · 输入时间");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, label + " · 滚轮选择");
        fields.Children.Add(input); Grid.SetColumn(picker, 1); fields.Children.Add(picker);
        Children.Add(fields);
        input.TextChanged += (_, _) =>
        {
            if (syncing || !TryGetTime(out var time)) return;
            syncing = true;
            try { picker.Time = time; }
            finally { syncing = false; }
        };
        picker.TimeChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            try { input.Text = picker.Time.ToString("hh\\:mm", CultureInfo.InvariantCulture); }
            finally { syncing = false; }
        };
        input.LostFocus += (_, _) =>
        {
            if (TryGetTime(out var time)) input.Text = time.ToString("hh\\:mm", CultureInfo.InvariantCulture);
        };
    }

    internal bool TryGetTime(out TimeSpan time) => TimeText.TryParse(input.Text, out time);
}

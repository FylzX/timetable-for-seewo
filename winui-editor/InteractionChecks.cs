using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Timetable.Editor;

public sealed partial class MainWindow
{
    internal async Task CheckTimeEntryAsync()
    {
        var entry = new TimeEntry("测试时间", new TimeSpan(8, 0, 0));
        var dialog = new ContentDialog { Title = "时间输入检查", Content = entry, CloseButtonText = "关闭" };
        var opened = new TaskCompletionSource<bool>();
        dialog.Opened += (_, _) => opened.TrySetResult(true);
        var pending = Dialog(dialog);
        try
        {
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var input = Descendants(entry).OfType<TextBox>().Single();
            var picker = Descendants(entry).OfType<TimePicker>().Single();
            input.Text = "8:30";
            await Task.Delay(100);
            if (!entry.TryGetTime(out var time) || time != new TimeSpan(8, 30, 0) || picker.Time != time)
                throw new InvalidOperationException("Typing a time did not update the picker.");
            picker.Time = new TimeSpan(9, 45, 0);
            await Task.Delay(100);
            if (input.Text != "09:45" || !entry.TryGetTime(out time) || time != picker.Time)
                throw new InvalidOperationException("Selecting a time did not update the input.");
            foreach (var invalid in new[] { "08:", "25:10", "" })
            {
                input.Text = invalid;
                await Task.Delay(100);
                if (input.Text != invalid || entry.TryGetTime(out _))
                    throw new InvalidOperationException("Invalid typed time was replaced with a previous valid value.");
            }
        }
        finally { dialog.Hide(); await pending; }
    }

    // Runs only with the explicit diagnostic argument. The fixture lives in
    // memory; original data and the saved baseline are restored before closing.
    internal async Task CheckScrollDeletionAsync()
    {
        var original = data;
        var selection = selectedExceptionKey;
        try
        {
            List<Lesson> Rows() => Enumerable.Range(0, 30).Select(index => new Lesson
            {
                Start = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(420 + index * 30)).ToString("HH:mm"),
                End = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(440 + index * 30)).ToString("HH:mm"),
                Course = "语文"
            }).ToList();
            data = new EditorData
            {
                Weekly = Enumerable.Range(1, 5).ToDictionary(day => day.ToString(), _ => Rows()),
                Exceptions = Enumerable.Range(1, 8).ToDictionary(day => new DateOnly(2026, 10, day).ToString("yyyy-MM-dd"), _ => Rows()),
                Template = []
            };
            RenderWeek(); RenderExceptions();
            foreach (var exceptions in new[] { false, true })
            {
                Tabs.SelectedIndex = exceptions ? 1 : 0;
                await Task.Delay(500);
                var scroll = (exceptions ? exceptionScroll : weeklyScroll)!;
                scroll.UpdateLayout();
                var vertical = Math.Min(500, scroll.ScrollableHeight);
                var horizontal = exceptions ? scroll.ScrollableWidth * .5 : 0;
                if (vertical < 100 || (exceptions && horizontal < 10))
                    throw new InvalidOperationException("Scroll fixture did not exceed the viewport.");
                scroll.ChangeView(horizontal, vertical, null, true);
                await Task.Delay(200);
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    var button = Descendants(scroll).OfType<Button>().First(candidate =>
                        AutomationProperties.GetName(candidate) == "删除课程" && candidate.ActualHeight > 0 &&
                        candidate.TransformToVisual(scroll).TransformPoint(new Point()).Y >= 0 &&
                        candidate.TransformToVisual(scroll).TransformPoint(new Point()).Y + candidate.ActualHeight < scroll.ViewportHeight);
                    button.Focus(FocusState.Programmatic);
                    await Task.Delay(100);
                    var beforeVertical = scroll.VerticalOffset;
                    var beforeHorizontal = scroll.HorizontalOffset;
                    var source = exceptions ? data.Exceptions : data.Weekly;
                    var beforeCount = source.Values.Sum(rows => rows.Count);
                    var peer = new ButtonAutomationPeer(button);
                    ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                    await Task.Delay(350);
                    if (source.Values.Sum(rows => rows.Count) != beforeCount - 1)
                        throw new InvalidOperationException("Delete button did not remove exactly one lesson.");
                    if (Math.Abs(scroll.VerticalOffset - beforeVertical) > 2 || Math.Abs(scroll.HorizontalOffset - beforeHorizontal) > 2)
                        throw new InvalidOperationException($"Deleting a lesson moved the viewport: {beforeHorizontal},{beforeVertical} -> {scroll.HorizontalOffset},{scroll.VerticalOffset}");
                }
                if (exceptions)
                {
                    var before = scroll.HorizontalOffset;
                    selectedExceptionKey = data.Exceptions.Keys.Last();
                    RenderExceptions();
                    await Task.Delay(300);
                    if (Math.Abs(scroll.HorizontalOffset - before) > 2)
                        throw new InvalidOperationException("Selecting a date reset horizontal scrolling.");
                }
            }
        }
        finally
        {
            data = original;
            selectedExceptionKey = selection;
            RenderWeek(); RenderExceptions(); Changed();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}

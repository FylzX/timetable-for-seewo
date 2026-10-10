using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Timetable.Editor;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Lesson : Observable
{
    private string start = "", end = "", course = "";
    [JsonPropertyName("start")]
    public string Start { get => start; set { start = value; Notify(); Notify(nameof(TimeLabel)); } }
    [JsonPropertyName("end")]
    public string End { get => end; set { end = value; Notify(); Notify(nameof(TimeLabel)); } }
    [JsonPropertyName("course")]
    public string Course { get => course; set { course = value; Notify(); } }
    [JsonIgnore] public string TimeLabel => $"{Start} – {End}";
    public Lesson Copy(bool blank = false) => new() { Start = Start, End = End, Course = blank ? "" : Course };
}

public sealed class DayColumn
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public bool IsException { get; init; }
    public ObservableCollection<Lesson> Rows { get; } = [];
    public string EmptyText => IsException ? "当天放假" : "暂无课程";
}

public sealed class EditorData
{
    public Dictionary<string, List<Lesson>> Weekly { get; set; } = [];
    public Dictionary<string, List<Lesson>> Exceptions { get; set; } = [];
    public List<Lesson> Template { get; set; } = [];
}

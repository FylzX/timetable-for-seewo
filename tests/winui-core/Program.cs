using Timetable.Editor;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "timetable-core-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool valid, string message)
{
    if (!valid) throw new Exception(message);
    checks++;
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new Exception(message);
}
try
{
    foreach (var (text, expected) in new[] { ("00:00", 0), ("23:59", 1439), ("8:30", 510), (" 08:30 ", 510) })
        Check(TimeText.TryParse(text, out var time) && time.TotalMinutes == expected, "Typed time rejected or parsed incorrectly: " + text);
    foreach (var text in new[] { "", "08:", "24:00", "08:60", "-1:30", "08:30:00", "8.30", "12:00 PM" })
        Check(!TimeText.TryParse(text, out _), "Invalid or incomplete typed time accepted: " + text);
    var weekly = new Dictionary<string, List<Lesson>>
    {
        ["1"] = [new() { Start = "08:00", End = "08:40", Course = "语文" }],
        ["4"] = [new() { Start = "09:00", End = "09:40", Course = "数学" }],
        ["6"] = [new() { Start = "10:00", End = "10:40", Course = "自定义课程" }]
    };
    File.WriteAllText(Path.Combine(root, "timetable.json"), JsonSerializer.Serialize(weekly, ScheduleFiles.Json));
    File.WriteAllText(Path.Combine(root, "exceptions.json"), "{\"2026-10-11\": []}");
    var store = new ScheduleFiles(root);
    var data = store.Read();
    Check(data.Weekly.Count == 6, "Missing workdays must become editable empty columns");
    Check(data.Template[0].Start == "09:00", "Shared template must originate from Thursday");
    Check(File.Exists(Path.Combine(root, "template.json")), "Missing template must be created immediately on read");
    Check(!File.ReadAllText(Path.Combine(root, "template.json")).Contains("course"), "Generated template must store only times");
    Check(data.Exceptions["2026-10-11"].Count == 0, "An empty override must remain a holiday");
    var copied = data.Weekly["1"][0].Copy(); copied.Course = "化学";
    Check(data.Weekly["1"][0].Course == "语文", "Copied lessons must be independent");
    Check(copied.Copy(true).Course == "", "Applying a template must generate blank courses");
    data.Weekly["1"][0].Course = "班会";
    store.Save(data);
    var loaded = store.Read();
    Check(loaded.Weekly["1"][0].Course == "班会", "Unicode custom courses must survive restart");
    Check(loaded.Weekly["6"][0].Course == "自定义课程", "Hidden weekend data must be preserved");
    Check(loaded.Template.Count == 1, "Shared template must persist");
    Check(!File.ReadAllText(Path.Combine(root, "template.json")).Contains("course"), "Template format must remain time-only");
    Reject(() => ScheduleFiles.ValidateRows([new() { Start = "25:00", End = "26:00", Course = "语文" }], "test"), "Invalid hours accepted");
    Reject(() => ScheduleFiles.ValidateRows([new() { Start = "09:00", End = "08:00", Course = "语文" }], "test"), "Reverse range accepted");
    var overlapping = new List<Lesson> { new() { Start = "08:00", End = "09:00", Course = "语文" }, new() { Start = "08:40", End = "09:40", Course = "数学" } };
    ScheduleFiles.ValidateRows(overlapping, "test");
    Check(ScheduleFiles.Overlaps(overlapping[0], overlapping[1]), "Partial overlap must trigger a warning");
    Check(ScheduleFiles.Overlaps(overlapping[0], overlapping[0].Copy()), "Identical ranges must trigger a warning");
    Check(ScheduleFiles.Overlaps(overlapping[0], new() { Start = "08:10", End = "08:20" }), "Contained ranges must trigger a warning");
    Check(!ScheduleFiles.Overlaps(overlapping[0], new() { Start = "09:00", End = "09:40" }), "Adjacent ranges must not trigger a warning");
    var overlapRoot = Path.Combine(root, "overlap"); Directory.CreateDirectory(overlapRoot);
    var overlapStore = new ScheduleFiles(overlapRoot);
    overlapStore.Save(new EditorData { Weekly = new() { ["1"] = overlapping },
        Exceptions = new() { ["2026-10-13"] = overlapping.Select(row => row.Copy()).ToList() },
        Template = overlapping.Select(row => row.Copy(true)).ToList() });
    var overlapReloaded = overlapStore.Read();
    Check(overlapReloaded.Weekly["1"].Count == 2 && overlapReloaded.Exceptions["2026-10-13"].Count == 2
        && overlapReloaded.Template.Count == 2, "Overlapping main, exception and template times must survive save and reload");
    data.Weekly["1"][0].Course = "";
    var prior = File.ReadAllBytes(Path.Combine(root, "timetable.json"));
    Reject(() => store.Save(data), "Empty course accepted");
    Check(prior.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "timetable.json"))), "Invalid save changed disk");
    data.Weekly["1"][0].Course = "体育";
    using (var blocked = new FileStream(Path.Combine(root, "exceptions.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        bool failed = false;
        try { store.Save(data); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed, "Locked exceptions file must fail the save");
        Check(prior.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "timetable.json"))), "Partial save must restore preceding file");
    }
    Check(!Directory.GetFiles(root, ".timetable-*.tmp").Any(), "Save must clean staging files");
    File.WriteAllText(Path.Combine(root, "exceptions.json"), "{\"2026-02-30\": []}");
    Reject(() => store.Read(), "Invalid exception date accepted");
    var parallel = new[]
    {
        new Lesson { Start = "07:10", End = "07:45", Course = "升旗" },
        new Lesson { Start = "07:15", End = "07:45", Course = "英语" },
        new Lesson { Start = "07:50", End = "08:30", Course = "数学" },
        new Lesson { Start = "08:40", End = "09:20", Course = "数学" },
        new Lesson { Start = "12:45", End = "13:05", Course = "自习" },
        new Lesson { Start = "12:50", End = "13:30", Course = "物理" },
        new Lesson { Start = "13:15", End = "14:15", Course = "等级组合2" },
        new Lesson { Start = "18:00", End = "19:00", Course = "语文" },
        new Lesson { Start = "19:15", End = "21:15", Course = "自习" }
    };
    var axis = new TimelineLayout(parallel);
    foreach (var height in new[] { 280.0, 540.0, 850.0 })
    {
        Check(axis.Position("07:45", height) == axis.Position(parallel[0].End, height)
            && axis.Position(parallel[0].End, height) == axis.Position(parallel[1].End, height), "Equal times must align across columns");
        Check(axis.Position("07:10", height) == 0 && axis.Position("21:15", height) == height, "Whole day must fit viewport");
        Check(axis.Minutes.Zip(axis.Minutes.Skip(1)).All(pair => axis.Position(pair.First, height) < axis.Position(pair.Second, height)), "Timeline must remain strictly chronological");
        Check(parallel.All(row => axis.Position(row.End, height) > axis.Position(row.Start, height)), "Every course must have positive height");
        Check(axis.Position("07:50", height) > axis.Position("07:45", height), "Real gaps must survive compression");
    }
    Check(axis.Position("18:00", 540) - axis.Position("14:15", 540) < 20, "Long empty breaks should be compressed");
    var midnight = new TimelineLayout([new() { Start = "00:00", End = "00:01", Course = "A" }, new() { Start = "00:01", End = "23:59", Course = "B" }]);
    Check(midnight.Position("00:01", 600) > 0 && midnight.Position("23:59", 600) == 600, "Extreme times must fit and adjacent edges must meet");
    var empty = new TimelineLayout([]);
    Check(empty.Position("08:00", 500) == 0 && empty.Position("17:00", 500) == 500, "Empty board needs a stable axis");
    // A full evening schedule with cross-day offsets, matching the layout that was cramped.
    // Test a fixed fixture rather than depending on the user's editable JSON files.
    string[] denseTimes = ["07:10-07:45", "07:15-07:45", "07:50-08:30", "08:40-09:20",
        "09:35-10:15", "10:25-11:45", "12:45-13:05", "12:45-13:25", "12:50-13:30",
        "13:15-14:15", "13:35-14:15", "13:40-14:20", "14:15-14:45", "14:30-15:10",
        "14:45-15:45", "14:45-16:45", "15:20-16:00", "15:55-16:55", "18:00-18:40",
        "18:00-19:00", "18:50-19:30", "19:15-20:15", "19:15-21:15", "19:40-20:20",
        "20:25-21:15", "20:30-21:15"];
    var dense = denseTimes.Select(range => new Lesson { Start = range[..5], End = range[6..], Course = "课程" }).ToArray();
    var denseAxis = new TimelineLayout(dense);
    Check(dense.All(row => denseAxis.Position(row.End, 700) - denseAxis.Position(row.Start, 700) - 4 >= 40),
        "Full-day mixed schedules must leave readable card space at the normal viewport");
    Check(dense.All(row => denseAxis.Position(row.End, 500) - denseAxis.Position(row.Start, 500) - 4 >= 24),
        "Smaller viewports must retain room for a compact single text row");
    Check(dense.All(row => denseAxis.Position(row.Start, 700) >= 0 && denseAxis.Position(row.End, 700) <= 700),
        "A dense full-day schedule must remain entirely in the viewport");
    var runtimeData = new EditorData
    {
        Weekly = new() { ["1"] = [new() { Start = "08:00", End = "08:40", Course = "语文" }, new() { Start = "08:50", End = "09:30", Course = "数学" }] },
        Exceptions = new() { ["2026-10-12"] = [], ["2026-10-13"] = [new() { Start = "10:00", End = "11:00", Course = "化学" }] }
    };
    var monday = new DateOnly(2026, 10, 19);
    var mondayRows = Timetable.Desktop.DailySchedule.Rows(runtimeData, monday);
    Check(mondayRows.Count == 2, "Runtime must map Monday to key 1");
    Check(Timetable.Desktop.DailySchedule.Rows(runtimeData, new(2026, 10, 12)).Count == 0, "Empty exception overrides weekly lessons");
    Check(Timetable.Desktop.DailySchedule.Rows(runtimeData, new(2026, 10, 13))[0].Course == "化学", "Exception lessons take priority");
    Check(Timetable.Desktop.DailySchedule.Rows(runtimeData, new(2026, 10, 18)).Count == 0, "An absent weekday is an empty day");
    Check(Timetable.Desktop.DailySchedule.Marker(mondayRows, monday, new(2026, 10, 19, 7, 59, 59)) == (0, false), "Before school points to the next lesson");
    Check(Timetable.Desktop.DailySchedule.Marker(mondayRows, monday, new(2026, 10, 19, 8, 0, 0)) == (0, true), "Start boundary is inclusive");
    Check(Timetable.Desktop.DailySchedule.Marker(mondayRows, monday, new(2026, 10, 19, 8, 40, 0)) == (1, false), "End boundary is exclusive and points to next lesson");
    Check(Timetable.Desktop.DailySchedule.Marker(mondayRows, monday, new(2026, 10, 19, 8, 50, 0)) == (1, true), "Second lesson start is inclusive");
    Check(Timetable.Desktop.DailySchedule.Marker(mondayRows, monday, new(2026, 10, 19, 9, 30, 0)) == null, "Last end clears the marker");
    Check(Timetable.Desktop.DailySchedule.Marker(mondayRows, monday, new(2026, 10, 20, 8, 10, 0)) == null, "Previewing another date cannot use today's marker");
    Check(Timetable.Desktop.DailySchedule.Marker([], monday, new(2026, 10, 19, 8, 10, 0)) == null, "Empty day has no marker");
    var clock = new Timetable.Desktop.SimulationClock();
    Check(!clock.Active, "Simulation defaults to computer time");
    var simulated = new DateTime(2026, 1, 1, 23, 59, 59);
    clock.Set(simulated);
    Check(clock.Active && clock.Now >= simulated && clock.Now < simulated.AddSeconds(1), "Simulated clock uses selected anchor");
    Thread.Sleep(30);
    Check(clock.Now >= simulated.AddMilliseconds(20), "Simulated clock continues advancing");
    clock.Reset();
    Check(!clock.Active && Math.Abs((clock.Now - DateTime.Now).TotalSeconds) < 1, "Reset returns to computer time");
    var placement = Timetable.Desktop.Placement.Capture(-1700, 80, 400, 700, -1920, 0, 1920, 1080, true);
    Check(placement.Restore(-1920, 0, 1920, 1080) == (-1700, 80, 400, 700), "Position survives roundtrip on a negative-coordinate monitor");
    var scaled = placement.Restore(0, 0, 3840, 2160);
    Check(scaled.Width == 800 && scaled.Height == 1400, "Placement scales with monitor work height");
    Check(scaled.X >= 0 && scaled.Y >= 0 && scaled.X + scaled.Width <= 3840 && scaled.Y + scaled.Height <= 2160, "Restored placement remains visible");
    var malformed = new Timetable.Desktop.Placement(double.NaN, double.PositiveInfinity, -50, 99);
    var safe = malformed.Restore(0, 0, 1280, 720);
    Check(safe.X >= 0 && safe.Y >= 0 && safe.X + safe.Width <= 1280 && safe.Y + safe.Height <= 720, "Invalid placement values are clamped");
    Check(new Timetable.Desktop.Placement().Restore(0, 0, 200, 200).Width <= 200, "Tiny display cannot exceed monitor bounds");
    var settingsDirectory = Path.Combine(root, "appearance");
    var settings = new DesktopSettings(settingsDirectory);
    Check(settings.ReadBackgroundOpacity() == DesktopSettings.DefaultBackgroundOpacity, "Missing appearance settings must retain the original background alpha");
    settings.SavePlacement(placement);
    Check(settings.ReadBackgroundOpacity() == DesktopSettings.DefaultBackgroundOpacity, "Old placement-only settings must keep the original appearance");
    var settingsPath = Path.Combine(settingsDirectory, "user-data", "settings-csharp.json");
    var settingsNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
    settingsNode["FutureSetting"] = "preserve me";
    File.WriteAllText(settingsPath, settingsNode.ToJsonString());
    foreach (var opacity in new[] { 0.0, 37.5, 100.0, DesktopSettings.DefaultBackgroundOpacity })
    {
        settings.SaveBackgroundOpacity(opacity);
        var reloadedSettings = new DesktopSettings(settingsDirectory);
        Check(reloadedSettings.ReadBackgroundOpacity() == opacity, "Opacity must survive restart including endpoints and exact default");
        var savedPlacement = JsonSerializer.Deserialize<Timetable.Desktop.Placement>(File.ReadAllText(settingsPath));
        Check(savedPlacement == placement, "Saving opacity must preserve window placement and lock state");
        settings.SavePlacement(placement with { Locked = false });
        Check(reloadedSettings.ReadBackgroundOpacity() == opacity, "Moving or locking the widget must preserve saved opacity");
        Check(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!["FutureSetting"]!.GetValue<string>() == "preserve me", "Saving settings must preserve unrelated fields");
        settings.SavePlacement(placement);
    }
    foreach (var invalid in new[] { -1.0, 101.0, double.NaN, double.PositiveInfinity })
    {
        var beforeSettings = File.ReadAllText(settingsPath);
        Reject(() => settings.SaveBackgroundOpacity(invalid), "Invalid opacity must not be saved");
        Check(File.ReadAllText(settingsPath) == beforeSettings, "Rejected opacity must leave settings untouched");
    }
    File.WriteAllText(settingsPath, "{broken");
    var malformedSettings = File.ReadAllText(settingsPath);
    try { settings.SaveBackgroundOpacity(50); throw new Exception("Malformed existing settings must not be silently overwritten"); }
    catch (JsonException) { checks++; }
    Check(File.ReadAllText(settingsPath) == malformedSettings, "Malformed settings must remain available for recovery");
    var freshDirectory = Path.Combine(root, "fresh");
    var freshStore = new ScheduleFiles(freshDirectory);
    var fresh = freshStore.Read();
    Check(new[] { "timetable.json", "exceptions.json", "template.json" }.All(name => File.Exists(Path.Combine(freshDirectory, name))), "Fresh portable directory creates all three JSON files");
    Check(fresh.Weekly.Count == 5 && fresh.Exceptions.Count == 0, "Fresh data contains five empty working days");
    Check(fresh.Template.Count == 0 && File.ReadAllText(Path.Combine(freshDirectory, "template.json")).Trim() == "[]", "Fresh template is a valid empty list");
    fresh.Weekly["1"].Add(new() { Start = "08:00", End = "08:40", Course = "英语" });
    freshStore.Save(fresh);
    Check(freshStore.Read().Weekly["1"][0].Course == "英语", "Existing JSON is never reset during initialization");
    foreach (var missing in new[] { "timetable.json", "exceptions.json", "template.json" })
    {
        var partialDirectory = Path.Combine(root, "missing-" + missing);
        Directory.CreateDirectory(partialDirectory);
        var content = new Dictionary<string, string>
        {
            ["timetable.json"] = "{\"1\":[{\"start\":\"08:00\",\"end\":\"08:40\",\"course\":\"英语\"}]}",
            ["exceptions.json"] = "{\"2026-10-12\":[]}",
            ["template.json"] = "[{\"start\":\"09:00\",\"end\":\"09:40\"}]"
        };
        foreach (var (name, text) in content.Where(pair => pair.Key != missing)) File.WriteAllText(Path.Combine(partialDirectory, name), text);
        new ScheduleFiles(partialDirectory).Read();
        Check(File.Exists(Path.Combine(partialDirectory, missing)), "Individually missing JSON must be restored: " + missing);
        Check(content.Where(pair => pair.Key != missing).All(pair => File.ReadAllText(Path.Combine(partialDirectory, pair.Key)) == pair.Value), "Initialization must preserve existing files exactly: " + missing);
    }
    fresh.Weekly["8"] = [];
    Reject(() => freshStore.Save(fresh), "Save must reject invalid weekday keys");
    fresh.Weekly.Remove("8"); fresh.Exceptions["2026-02-30"] = [];
    Reject(() => freshStore.Save(fresh), "Save must reject invalid exception date keys");
    Console.WriteLine($"{checks} C# data, timeline and runtime checks passed");
}
catch (Exception error)
{
    // Report failed checks in the terminal instead of triggering a Windows crash dialog.
    Console.Error.WriteLine(error.ToString());
    Environment.ExitCode = 1;
}
finally
{
    // This directory was uniquely created above for these tests.
    Directory.Delete(root, true);
}

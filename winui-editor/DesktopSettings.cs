using System.Text.Json;
using System.Text.Json.Nodes;
using System.ComponentModel;

namespace Timetable.Editor;

internal sealed record DesktopPreferences(double BackgroundOpacity, string[] ContextMenuOrder,
    string[] HiddenContextMenuItems, bool UpdateBadgeEnabled);

internal static class UpdateBadgePolicy
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(3);
    public static bool ShouldShow(bool newerVersionAvailable, bool enabled) => newerVersionAvailable && enabled;
}

public sealed class MenuOption(string id, string label, bool enabled = true, bool canToggle = true) : INotifyPropertyChanged
{
    private bool isEnabled = enabled;
    public string Id { get; } = id;
    public string Label { get; } = label;
    public bool CanToggle { get; } = canToggle;
    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (isEnabled == value || !CanToggle) return;
            isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

// Shared by the desktop widget and the standalone editor. Merge each owner's
// fields with the latest file so saving window placement cannot undo settings.
internal sealed class DesktopSettings(string directory)
{
    public const double DefaultBackgroundOpacity = 242.0 / 255 * 100;
    public static readonly (string Id, string Label)[] ConfigurableMenuItems =
    [
        ("edit", "编辑课表"),
        ("test-time", "调整时间(测试用)"),
        ("lock", "锁定位置和大小"),
        ("view-schedule", "查看课表"),
        ("reset-placement", "恢复默认位置和大小"),
        ("reload-schedule", "重新读取课表"),
        ("check-updates", "检查更新")
    ];
    public const string ExitMenuItem = "退出";
    private static readonly string[] DefaultMenuOrder = ConfigurableMenuItems.Select(item => item.Id).ToArray();
    private readonly string path = Path.Combine(directory, "user-data", "settings-csharp.json");

    public double ReadBackgroundOpacity()
    {
        var value = Read()["BackgroundOpacity"];
        return value is JsonValue number && number.TryGetValue<double>(out var opacity) && double.IsFinite(opacity)
            ? Math.Clamp(opacity, 0, 100) : DefaultBackgroundOpacity;
    }

    public void SaveBackgroundOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || opacity < 0 || opacity > 100)
            throw new InvalidDataException("背景不透明度必须在 0% 到 100% 之间");
        Merge(new JsonObject { ["BackgroundOpacity"] = opacity });
    }

    public DesktopPreferences ReadPreferences()
    {
        var root = Read();
        var opacityValue = root["BackgroundOpacity"];
        var opacity = opacityValue is JsonValue number && number.TryGetValue<double>(out var configured)
            && double.IsFinite(configured) ? Math.Clamp(configured, 0, 100) : DefaultBackgroundOpacity;
        var order = ReadStringArray(root["ContextMenuOrder"]);
        var validIds = DefaultMenuOrder.ToHashSet(StringComparer.Ordinal);
        var normalizedOrder = order.Where(validIds.Contains).Distinct(StringComparer.Ordinal).ToList();
        normalizedOrder.AddRange(DefaultMenuOrder.Where(id => !normalizedOrder.Contains(id, StringComparer.Ordinal)));
        var hidden = ReadStringArray(root["HiddenContextMenuItems"])
            .Where(validIds.Contains).Distinct(StringComparer.Ordinal).ToArray();
        var badgeEnabled = root["UpdateBadgeEnabled"] is not JsonValue badge || !badge.TryGetValue<bool>(out var enabled) || enabled;
        return new(opacity, normalizedOrder.ToArray(), hidden, badgeEnabled);
    }

    public static string[] VisibleContextMenuItems(DesktopPreferences preferences, bool expanded) =>
        preferences.ContextMenuOrder.Where(id => expanded || !preferences.HiddenContextMenuItems.Contains(id, StringComparer.Ordinal)).ToArray();

    public void SavePreferences(DesktopPreferences preferences)
    {
        if (!double.IsFinite(preferences.BackgroundOpacity) || preferences.BackgroundOpacity < 0 || preferences.BackgroundOpacity > 100)
            throw new InvalidDataException("背景不透明度必须在 0% 到 100% 之间");
        var validIds = DefaultMenuOrder.ToHashSet(StringComparer.Ordinal);
        var order = preferences.ContextMenuOrder.Where(validIds.Contains).Distinct(StringComparer.Ordinal).ToList();
        order.AddRange(DefaultMenuOrder.Where(id => !order.Contains(id, StringComparer.Ordinal)));
        var hidden = preferences.HiddenContextMenuItems.Where(validIds.Contains).Distinct(StringComparer.Ordinal).ToArray();
        Merge(new JsonObject
        {
            ["BackgroundOpacity"] = preferences.BackgroundOpacity,
            ["ContextMenuOrder"] = JsonSerializer.SerializeToNode(order),
            ["HiddenContextMenuItems"] = JsonSerializer.SerializeToNode(hidden),
            ["UpdateBadgeEnabled"] = preferences.UpdateBadgeEnabled
        });
    }

    public void SavePlacement<T>(T placement) => Merge(JsonSerializer.SerializeToNode(placement)!.AsObject());

    private JsonObject Read()
    {
        if (!File.Exists(path)) return new();
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new JsonException("窗口设置文件应为 JSON 对象");
    }

    private static string[] ReadStringArray(JsonNode? node) => node is JsonArray array
        ? array.OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var item) ? item : null)
            .Where(item => item != null).Cast<string>().ToArray()
        : [];

    private void Merge(JsonObject changes)
    {
        var current = Read();
        foreach (var entry in changes) current[entry.Key] = entry.Value?.DeepClone();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, current.ToJsonString() + "\n");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

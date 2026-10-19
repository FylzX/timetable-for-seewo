using System.Text.Json;
using System.Text.Json.Nodes;

namespace Timetable.Editor;

// Shared by the desktop widget and the standalone editor. Merge each owner's
// fields with the latest file so saving window placement cannot undo settings.
internal sealed class DesktopSettings(string directory)
{
    public const double DefaultBackgroundOpacity = 242.0 / 255 * 100;
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

    public void SavePlacement<T>(T placement) => Merge(JsonSerializer.SerializeToNode(placement)!.AsObject());

    private JsonObject Read()
    {
        if (!File.Exists(path)) return new();
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new JsonException("窗口设置文件应为 JSON 对象");
    }

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

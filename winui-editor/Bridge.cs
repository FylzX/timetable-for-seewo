using System.Text.Json;

namespace Timetable.Editor;

public sealed class Bridge(bool enabled)
{
    public void Send(string type, bool? dirty = null)
    {
        if (!enabled) return;
        try
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { type, dirty }));
            Console.Out.Flush();
        }
        catch (IOException) { }
    }

    public void Start(Action<string> received)
    {
        if (!enabled) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await Console.In.ReadLineAsync() is { } line)
                {
                    try
                    {
                        using var message = JsonDocument.Parse(line);
                        if (message.RootElement.TryGetProperty("type", out var type))
                            received(type.GetString() ?? "");
                    }
                    catch (JsonException) { }
                }
            }
            catch (IOException) { }
        });
    }
}

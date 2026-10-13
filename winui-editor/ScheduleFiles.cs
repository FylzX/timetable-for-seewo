using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Timetable.Editor;

public sealed class ScheduleFiles(string directory)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private string FilePath(string name) => Path.Combine(directory, name);

    public EditorData Read()
    {
        Directory.CreateDirectory(directory);
        EnsureJsonFile("timetable.json", "{}\n");
        EnsureJsonFile("exceptions.json", "{}\n");
        var data = new EditorData
        {
            Weekly = ReadJson<Dictionary<string, List<Lesson>>>("timetable.json"),
            Exceptions = ReadJson<Dictionary<string, List<Lesson>>>("exceptions.json")
        };
        foreach (var (key, rows) in data.Weekly)
        {
            if (key.Length != 1 || key[0] < '1' || key[0] > '7')
                throw new InvalidDataException($"无效的星期：{key}");
            ValidateRows(rows, key);
        }
        foreach (var (key, rows) in data.Exceptions)
        {
            if (!DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new InvalidDataException($"无效的日期：{key}");
            ValidateRows(rows, key);
        }
        for (var day = 1; day <= 5; day++) data.Weekly.TryAdd(day.ToString(), []);
        if (File.Exists(FilePath("template.json")))
            data.Template = ReadJson<List<Lesson>>("template.json");
        else
        {
            var source = data.Weekly.GetValueOrDefault("4");
            if (source == null || source.Count == 0) source = data.Weekly.Values.FirstOrDefault(rows => rows.Count > 0);
            data.Template = source?.Select(row => row.Copy(true)).ToList() ?? [];
        }
        ValidateRows(data.Template, "时间模板", true);
        EnsureJsonFile("template.json", JsonSerializer.Serialize(data.Template.Select(row => new { start = row.Start, end = row.End }), Json) + "\n");
        return data;
    }

    private void EnsureJsonFile(string name, string content)
    {
        var path = FilePath(name);
        if (!File.Exists(path)) File.WriteAllText(path, content, Encoding.UTF8);
    }

    private T ReadJson<T>(string name) => JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath(name)), Json)
        ?? throw new InvalidDataException($"{name} 内容为空");

    public static void ValidateRows(IEnumerable<Lesson> rows, string name, bool template = false)
    {
        if (rows is null) throw new InvalidDataException($"{name}：课程列表为空值");
        var index = 0;
        foreach (var row in rows)
        {
            index++;
            if (row == null || !TimeOnly.TryParseExact(row.Start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || !TimeOnly.TryParseExact(row.End, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                throw new InvalidDataException($"{name} 第 {index} 节：时间须为 HH:mm");
            if (start >= end)
                throw new InvalidDataException($"{name} 第 {index} 节：开始须早于结束");
            if (!template && string.IsNullOrWhiteSpace(row.Course))
                throw new InvalidDataException($"{name} 第 {index} 节：请选择课程、输入自定义名称，或删除卡片");
        }
    }

    public static bool Overlaps(Lesson first, Lesson second) =>
        TimeOnly.ParseExact(first.Start, "HH:mm", CultureInfo.InvariantCulture) < TimeOnly.ParseExact(second.End, "HH:mm", CultureInfo.InvariantCulture)
        && TimeOnly.ParseExact(second.Start, "HH:mm", CultureInfo.InvariantCulture) < TimeOnly.ParseExact(first.End, "HH:mm", CultureInfo.InvariantCulture);

    public void Save(EditorData data)
    {
        ValidateForSave(data);
        var payloads = new Dictionary<string, byte[]>
        {
            [FilePath("timetable.json")] = Encode(data.Weekly),
            [FilePath("exceptions.json")] = Encode(data.Exceptions),
            [FilePath("template.json")] = Encode(data.Template.Select(row => new { start = row.Start, end = row.End }))
        };
        var originals = payloads.Keys.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        List<string> completed = [];
        try
        {
            foreach (var (path, bytes) in payloads)
            {
                Replace(path, bytes);
                completed.Add(path);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            List<string> unrestored = [];
            foreach (var path in Enumerable.Reverse(completed))
                try
                {
                    if (originals[path] is { } bytes) Replace(path, bytes);
                    else File.Delete(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { unrestored.Add(Path.GetFileName(path)); }
            if (unrestored.Count > 0)
                throw new IOException("保存失败，以下文件无法还原：" + string.Join("、", unrestored), failure);
            throw;
        }
    }

    /// <summary>Runs every save-time invariant before touching any file.</summary>
    public static void ValidateForSave(EditorData data)
    {
        if (data == null) throw new InvalidDataException("课表数据为空");
        if (data.Weekly == null || data.Exceptions == null) throw new InvalidDataException("课表数据为空");
        foreach (var (key, rows) in data.Weekly)
        {
            if (key.Length != 1 || key[0] < '1' || key[0] > '7') throw new InvalidDataException($"无效的星期：{key}");
            ValidateRows(rows, "星期 " + key);
        }
        foreach (var (key, rows) in data.Exceptions)
        {
            if (!DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new InvalidDataException($"无效的日期：{key}");
            ValidateRows(rows, key);
        }
        ValidateRows(data.Template, "时间模板", true);
    }

    public static string Fingerprint(EditorData data) => JsonSerializer.Serialize(data, Json);
    private static byte[] Encode<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json) + "\n");
    private static void Replace(string path, byte[] content)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".timetable-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            { stream.Write(content); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

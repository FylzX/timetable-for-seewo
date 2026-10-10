namespace Timetable.Desktop;

internal static class StartupLog
{
    public static bool Diagnostic => Environment.GetCommandLineArgs().Any(arg => arg is "--diagnostic" or "--smoke-test" or "--interaction-test");
    public static string FilePath
    {
        get
        {
            var location = new DirectoryInfo(AppContext.BaseDirectory);
            var root = location.Name.Equals("app", StringComparison.OrdinalIgnoreCase) ? location.Parent!.FullName : location.FullName;
            return Path.Combine(root, "user-data", "startup.log");
        }
    }
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 256 * 1024)
                File.Move(FilePath, FilePath + ".previous", true);
            File.AppendAllText(FilePath, $"{DateTime.Now:O} [{Environment.ProcessId}] {message}\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static void Fail(Exception error)
    {
        Write("Application failed: " + error);
        foreach (System.Collections.DictionaryEntry item in error.Data)
            Write("Exception detail: " + item.Key + " = " + item.Value);
        Environment.ExitCode = 1;
        if (!Diagnostic)
            MessageBox(0, "课表启动或运行失败。\n\n" + error.Message + "\n\n诊断日志：" + FilePath,
                "桌面课表", 0x10);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint window, string text, string caption, uint type);
}

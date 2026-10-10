// Built separately with Windows' .NET Framework compiler, not into the WinUI app.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

internal static class PortableLauncher
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool diagnostic = args.Contains("--diagnostic") || args.Contains("--smoke-test") || args.Contains("--interaction-test");
        string root = AppDomain.CurrentDomain.BaseDirectory;
        try
        {
            string executable = Path.Combine(root, "app", "DesktopTimetable.exe");
            if (!File.Exists(executable) || !File.Exists(Path.Combine(root, "app", "resources.pri")))
                throw new FileNotFoundException("程序文件不完整，请移动整个 release 文件夹，保留 app 子目录。");
            string arguments = args.Contains("--data-dir") ? "" : "--data-dir " + Quote(Path.GetFullPath(root)) + " ";
            arguments += string.Join(" ", args.Select(Quote));
            using (Process child = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = root,
                UseShellExecute = false
            }))
            {
                if (args.Contains("--smoke-test") || args.Contains("--interaction-test")) child.WaitForExit();
                else if (!child.WaitForExit(4000)) return 0;
                if (child.ExitCode != 0 && !diagnostic)
                    MessageBox.Show("课表启动失败。请查看诊断日志：\n" + Path.Combine(root, "user-data", "startup.log"),
                        "桌面课表", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return child.ExitCode;
            }
        }
        catch (Exception error)
        {
            if (!diagnostic) MessageBox.Show(error.Message, "桌面课表", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    // Quote each Windows argument, including trailing backslashes in folder paths.
    private static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char value in argument)
        {
            if (value == '\\') { slashes++; continue; }
            if (value == '"') result.Append('\\', slashes * 2 + 1);
            else result.Append('\\', slashes);
            result.Append(value);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }
}

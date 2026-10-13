// Runs in a temporary copy of the portable launcher, after the WinUI app exits.
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Timetable.Updates;

internal static class PortableUpdateInstaller
{
    internal static bool Recover(string root, string[] arguments)
    {
        var work = Path.Combine(root, ".update");
        if (!Directory.Exists(work)) return true;
        UpdatePackage.CheckLinks(work);
        if (File.Exists(Path.Combine(work, "install.lock")))
        {
            try { using (File.Open(Path.Combine(work, "install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
            catch (IOException) { return false; }
        }
        if (File.Exists(Path.Combine(work, "transaction.flag")))
        {
            if (File.Exists(Path.Combine(work, "committed.flag"))) Cleanup(root);
            else
            {
                var backup = Path.Combine(work, "old-launcher.exe");
                if (File.Exists(backup) && UpdatePackage.Hash(backup) != UpdatePackage.Hash(Path.Combine(root, "DesktopTimetable.exe")))
                {
                    // The current launcher cannot overwrite itself. Hand recovery
                    // to a copy, exit, and let that copy restart the launcher.
                    var helper = Path.Combine(work, "Recovery.exe");
                    File.Copy(Path.Combine(root, "DesktopTimetable.exe"), helper, true);
                    File.WriteAllLines(Path.Combine(work, "recovery-arguments.txt"), arguments);
                    Process.Start(new ProcessStartInfo(helper, "--recover-update " + Quote(root) + " " + Process.GetCurrentProcess().Id)
                        { WorkingDirectory = root, UseShellExecute = false });
                    return false;
                }
                Restore(root); Cleanup(root); Log(root, "Recovered interrupted update");
            }
        }
        return true;
    }

    internal static int RecoverFromCopy(string root, int parentPid)
    {
        var work = Path.Combine(root, ".update");
        UpdatePackage.CheckLinks(work);
        if (!String.Equals(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), work, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("恢复工具路径无效。");
        try { using (var parent = Process.GetProcessById(parentPid)) if (!parent.WaitForExit(30000)) throw new IOException("启动器尚未退出。"); }
        catch (ArgumentException) { }
        Restore(root); Cleanup(root); Log(root, "Recovered interrupted update including launcher");
        var args = File.ReadAllLines(Path.Combine(work, "recovery-arguments.txt"));
        File.Delete(Path.Combine(work, "recovery-arguments.txt"));
        Process.Start(new ProcessStartInfo(Path.Combine(root, "DesktopTimetable.exe"), String.Join(" ", Array.ConvertAll(args, Quote)))
            { WorkingDirectory = root, UseShellExecute = false });
        return 0;
    }

    internal static int Run(string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var work = Path.Combine(root, ".update");
        if (!String.Equals(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), work, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新工具必须从便携目录内运行。");
        UpdatePackage.CheckLinks(work);
        var request = UpdatePackage.ReadXml<UpdateRequest>(Path.Combine(work, "pending.xml"));
        var stage = Path.GetFullPath(request.Stage);
        if (!stage.StartsWith(work + Path.DirectorySeparatorChar + "download-", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(stage) != "stage") throw new InvalidDataException("更新暂存路径无效。");
        Process child = null;
        var success = false;
        try
        {
            using (var gate = new FileStream(Path.Combine(work, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                UpdatePackage.Validate(stage, request.Version);
                WaitForParent(root, request);
                if (Directory.Exists(Path.Combine(work, "old-app"))) throw new IOException("上次更新尚未恢复，请重新启动课表。");
                File.WriteAllText(Path.Combine(work, "transaction.flag"), request.Version);
                Directory.Move(Path.Combine(root, "app"), Path.Combine(work, "old-app"));
                File.Copy(Path.Combine(root, "DesktopTimetable.exe"), Path.Combine(work, "old-launcher.exe"), true);
                Directory.Move(Path.Combine(stage, "app"), Path.Combine(root, "app"));
                ReplaceLauncher(root, Path.Combine(stage, "DesktopTimetable.exe"));
                var health = Path.Combine(work, "health-" + Guid.NewGuid().ToString("N") + ".txt");
                var arguments = "--data-dir " + Quote(root) + " --update-health " + Quote(health);
                if (!String.IsNullOrEmpty(request.TestUrl)) arguments += " --update-test-url " + Quote(request.TestUrl);
                if (request.SmokeTest) arguments += " --smoke-test";
                child = Process.Start(new ProcessStartInfo(Path.Combine(root, "app", "DesktopTimetable.exe"), arguments)
                    { WorkingDirectory = root, UseShellExecute = false });
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (true)
                {
                    if (File.Exists(health) && File.ReadAllText(health).Trim() == request.Version) break;
                    if (child.HasExited || DateTime.UtcNow >= deadline) throw new IOException("新版程序未通过启动检查。");
                    Thread.Sleep(100);
                }
                File.Delete(health);
                File.WriteAllText(Path.Combine(work, "committed.flag"), request.Version);
                success = true;
                Log(root, "UPDATE SUCCEEDED " + request.Version);
            }
            Cleanup(root);
            return 0;
        }
        catch (Exception error)
        {
            if (child != null && !child.HasExited) { child.Kill(); child.WaitForExit(5000); }
            // A competing updater owns the transaction; never roll it back.
            bool ownsTransaction = false;
            try { using (File.Open(Path.Combine(work, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) ownsTransaction = true; }
            catch (IOException) { }
            if (ownsTransaction && !success)
            {
                try
                {
                    Restore(root); Cleanup(root);
                    Log(root, "UPDATE FAILED; OLD VERSION RESTORED: " + error.Message);
                    if (!request.SmokeTest)
                        Process.Start(new ProcessStartInfo(Path.Combine(root, "DesktopTimetable.exe"), String.IsNullOrEmpty(request.TestUrl)
                            ? "" : "--update-test-url " + Quote(request.TestUrl)) { WorkingDirectory = root, UseShellExecute = false });
                }
                catch (Exception recoveryError) { Log(root, "Recovery needs retry: " + recoveryError.Message); }
            }
            if (!request.SmokeTest) System.Windows.Forms.MessageBox.Show("更新未完成，已尝试恢复旧版。\n\n" + error.Message
                + "\n\n课表文件没有被替换。详情：user-data/update.log", "课表更新", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
            return 1;
        }
        finally { if (child != null) child.Dispose(); }
    }

    private static void WaitForParent(string root, UpdateRequest request)
    {
        if (request.ParentPid <= 0) return;
        Process parent;
        try { parent = Process.GetProcessById(request.ParentPid); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks) return;
            if (!String.Equals(parent.MainModule.FileName, Path.Combine(root, "app", "DesktopTimetable.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("等待退出的进程不是当前课表程序。");
            if (!parent.WaitForExit(30000)) throw new IOException("课表尚未退出，更新已取消。");
        }
    }
    private static void Restore(string root)
    {
        var work = Path.Combine(root, ".update");
        var backup = Path.Combine(work, "old-app");
        if (Directory.Exists(backup))
        {
            var app = Path.Combine(root, "app");
            if (Directory.Exists(app)) Retry(delegate { Directory.Delete(app, true); });
            Directory.Move(backup, app);
        }
        var launcher = Path.Combine(work, "old-launcher.exe");
        var target = Path.Combine(root, "DesktopTimetable.exe");
        if (File.Exists(launcher) && (!File.Exists(target) || UpdatePackage.Hash(launcher) != UpdatePackage.Hash(target)))
            ReplaceLauncher(root, launcher);
    }
    private static void ReplaceLauncher(string root, string source)
    {
        var next = Path.Combine(root, ".update", "launcher-next.exe");
        File.Copy(source, next, true);
        var target = Path.Combine(root, "DesktopTimetable.exe");
        // Replace the directory entry atomically; interrupted writes cannot
        // leave the root launcher half-written and impossible to reopen.
        Retry(delegate { if (File.Exists(target)) File.Replace(next, target, null); else File.Move(next, target); });
    }
    private static void Cleanup(string root)
    {
        var work = Path.Combine(root, ".update");
        // Cleanup failure after a committed update is harmless and is retried on
        // the next launch; it must not be mistaken for an installation failure.
        try
        {
            var backup = Path.Combine(work, "old-app");
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            var pending = Path.Combine(work, "pending.xml");
            if (File.Exists(pending))
            {
                var request = UpdatePackage.ReadXml<UpdateRequest>(pending);
                var stage = Path.GetFullPath(request.Stage);
                if (stage.StartsWith(work + Path.DirectorySeparatorChar + "download-", StringComparison.OrdinalIgnoreCase))
                {
                    var job = Path.GetDirectoryName(stage);
                    if (Directory.Exists(job)) Directory.Delete(job, true);
                }
            }
            foreach (var file in new[] { "pending.xml", "old-launcher.exe", "transaction.flag", "committed.flag" })
            {
                var path = Path.Combine(work, file);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
            try { action(); return; }
            catch (IOException) { if (attempt >= 20) throw; Thread.Sleep(100); }
    }
    private static void Log(string root, string text)
    {
        try
        {
            var folder = Path.Combine(root, "user-data"); Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "update.log"), DateTime.Now.ToString("O") + " " + text + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static string Quote(string text)
    {
        var output = new StringBuilder("\""); int slashes = 0;
        foreach (var value in text)
        {
            if (value == '\\') { slashes++; continue; }
            output.Append('\\', slashes * (value == '"' ? 2 : 1) + (value == '"' ? 1 : 0));
            slashes = 0; output.Append(value);
        }
        return output.Append('\\', slashes * 2).Append('"').ToString();
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Timetable.Desktop;
using Timetable.Updates;

var lab = Path.GetFullPath(args[0]);
var service = new Uri(args[1]);
using var control = new HttpClient();
using var client = new UpdateClient(service);
var results = new List<object>();
var cases = Path.Combine(lab, "cases"); Directory.CreateDirectory(cases);
var scratch = Path.Combine(cases, "downloads"); Directory.CreateDirectory(scratch);
var failures = 0;
async Task Scenario(string name) => (await control.GetAsync(new Uri(service, "scenario?name=" + name))).EnsureSuccessStatusCode();
void Check(bool success, string description) { if (!success) throw new Exception(description); }
async Task Case(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, passed = true, detail = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; results.Add(new { name, passed = false, detail = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
async Task Reject(Func<Task> action, string message)
{
    bool rejected = false;
    try { await action(); } catch (Exception error) when (error is HttpRequestException or InvalidDataException or OperationCanceledException or IOException) { rejected = true; }
    Check(rejected, message);
}
void CopyFolder(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
    foreach (var folder in Directory.GetDirectories(source))
        if (Path.GetFileName(folder) != ".update") CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
}
string Prepare(string name)
{
    var destination = Path.Combine(cases, name + "-" + Guid.NewGuid().ToString("N"));
    CopyFolder(Path.Combine(lab, "v1.1.0"), destination);
    Directory.CreateDirectory(Path.Combine(destination, "user-data"));
    File.WriteAllText(Path.Combine(destination, "user-data", "settings-csharp.json"), "{\"Width\":0.36,\"Height\":0.60,\"X\":0.8,\"Y\":0.1,\"Locked\":true}\n");
    File.WriteAllText(Path.Combine(destination, "user-note.txt"), "Keep this unrelated portable file");
    return destination;
}
Dictionary<string, string> DataHashes(string root) => new[] { "timetable.json", "exceptions.json", "template.json", "user-data/settings-csharp.json", "user-note.txt" }
    .ToDictionary(name => name, name => UpdatePackage.Hash(Path.Combine(root, name)));
void Unchanged(string root, Dictionary<string, string> before) => Check(before.All(item => UpdatePackage.Hash(Path.Combine(root, item.Key)) == item.Value), "User data changed");
string VersionAt(string root)
{
    var info = FileVersionInfo.GetVersionInfo(Path.Combine(root, "app", "DesktopTimetable.dll"));
    return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
}
async Task<int> Apply(string root, string stage, Process? parent = null, string version = "1.1.1")
{
    var work = Path.Combine(root, ".update"); Directory.CreateDirectory(work);
    var updater = Path.Combine(work, "Updater.exe"); File.Copy(Path.Combine(root, "DesktopTimetable.exe"), updater, true);
    UpdatePackage.WriteXml(Path.Combine(work, "pending.xml"), new UpdateRequest { Version = version, Stage = stage,
        ParentPid = parent?.Id ?? 0, ParentStartTicks = parent?.StartTime.ToUniversalTime().Ticks ?? 0,
        TestUrl = service.ToString(), SmokeTest = true });
    using var process = Process.Start(new ProcessStartInfo(updater) { UseShellExecute = false, WorkingDirectory = root,
        ArgumentList = { "--apply-update", root } })!;
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    await process.WaitForExitAsync(limit.Token);
    // A successful updater leaves the real WinUI smoke-test process running
    // briefly; wait until its startup check has closed it before removing files.
    if (process.ExitCode == 0) await Task.Delay(4500);
    return process.ExitCode;
}
async Task<string> Download(string scenario, string root)
{
    await Scenario(scenario);
    var check = await client.CheckAsync("1.1.0", default);
    Check(check.Update != null, "Expected a newer version");
    return await client.DownloadAsync(check.Update!, root, null, default);
}

foreach (var (scenario, remote) in new[] { ("latest", "1.1.0"), ("older", "1.0.9"), ("no-release", (string?)null) })
    await Case(scenario, async () => { await Scenario(scenario); var check = await client.CheckAsync("1.1.0", default);
        Check(check.Current == "1.1.0" && check.Remote == remote && check.Update == null, "Wrong no-update status/version"); });
foreach (var scenario in new[] { "api-error", "missing-asset", "invalid-version" })
    await Case(scenario, async () => { await Scenario(scenario); await Reject(async () => { await client.CheckAsync("1.1.0", default); }, "Invalid release accepted"); });
await Case("timeout", async () => { await Scenario("timeout"); using var quick = new UpdateClient(service, TimeSpan.FromMilliseconds(400));
    await Reject(async () => { await quick.CheckAsync("1.1.0", default); }, "Timeout not reported"); });
await Case("user-cancels-before-download", async () => { await Scenario("newer"); var check = await client.CheckAsync("1.1.0", default);
    var work = Path.Combine(scratch, ".update");
    Check(check.Update?.Version == "1.1.1" && (!Directory.Exists(work) || !Directory.EnumerateFileSystemEntries(work).Any()), "Checking alone downloaded or changed files"); });
await Case("checksum-fallback", async () => { var stage = await Download("checksum-fallback", scratch); UpdatePackage.Validate(stage, "1.1.1"); Directory.Delete(Path.GetDirectoryName(stage)!, true); });
foreach (var scenario in new[] { "corrupt", "truncated", "unsafe-package", "wrong-version" })
    await Case(scenario, async () => { await Reject(async () => { await Download(scenario, scratch); }, "Invalid package accepted");
        Check(!Directory.GetDirectories(Path.Combine(scratch, ".update"), "download-*").Any(), "Failed download left staging files"); });
await Case("user-cancels-download", async () => { await Scenario("slow"); var check = await client.CheckAsync("1.1.0", default);
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
    await Reject(async () => { await client.DownloadAsync(check.Update!, scratch, null, cancel.Token); }, "Canceled download continued");
    Check(!Directory.GetDirectories(Path.Combine(scratch, ".update"), "download-*").Any(), "Canceled download left staging files"); });
await Case("successful-real-winui-update", async () => {
    var root = Prepare("success"); var before = DataHashes(root);
    var stage = await Download("newer", root); Check(await Apply(root, stage) == 0, "Updater returned failure");
    Check(VersionAt(root) == "1.1.1", "Application was not replaced with v1.1.1");
    Check(File.ReadAllText(Path.Combine(root, "user-data", "update.log")).Contains("UPDATE SUCCEEDED"), "No successful restart acknowledgement");
    Unchanged(root, before); Check(!Directory.Exists(Path.Combine(root, ".update", "old-app")), "Backup was not cleaned");
});
await Case("new-version-startup-fails-rolls-back", async () => {
    var root = Prepare("rollback"); var before = DataHashes(root); var launcher = UpdatePackage.Hash(Path.Combine(root, "DesktopTimetable.exe"));
    var stage = await Download("failed-startup", root); Check(await Apply(root, stage) != 0, "Broken app reported success");
    Check(VersionAt(root) == "1.1.0" && UpdatePackage.Hash(Path.Combine(root, "DesktopTimetable.exe")) == launcher, "Old binaries not restored");
    Unchanged(root, before);
});
await Case("launcher-file-locked-then-recovery", async () => {
    var root = Prepare("locked"); var before = DataHashes(root); var stage = await Download("newer", root);
    using (var locked = new FileStream(Path.Combine(root, "DesktopTimetable.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        Check(await Apply(root, stage) != 0, "Locked launcher was overwritten");
    using var process = Process.Start(new ProcessStartInfo(Path.Combine(root, "DesktopTimetable.exe")) { UseShellExecute = false, WorkingDirectory = root,
        ArgumentList = { "--smoke-test", "--data-dir", root } })!;
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await process.WaitForExitAsync(limit.Token);
    Check(process.ExitCode == 0 && VersionAt(root) == "1.1.0", "Restart did not recover old version"); Unchanged(root, before);
});
await Case("interrupted-install-recovered-on-launch", async () => {
    var root = Prepare("interrupted"); var before = DataHashes(root); var work = Path.Combine(root, ".update"); Directory.CreateDirectory(work);
    File.WriteAllText(Path.Combine(work, "transaction.flag"), "1.1.1");
    Directory.Move(Path.Combine(root, "app"), Path.Combine(work, "old-app"));
    File.Copy(Path.Combine(root, "DesktopTimetable.exe"), Path.Combine(work, "old-launcher.exe"));
    Directory.CreateDirectory(Path.Combine(root, "app")); File.WriteAllText(Path.Combine(root, "app", "partial.txt"), "interrupted");
    using var process = Process.Start(new ProcessStartInfo(Path.Combine(root, "DesktopTimetable.exe")) { UseShellExecute = false, WorkingDirectory = root,
        ArgumentList = { "--smoke-test", "--data-dir", root } })!;
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await process.WaitForExitAsync(limit.Token);
    Check(process.ExitCode == 0 && VersionAt(root) == "1.1.0", "Interrupted update did not recover"); Unchanged(root, before);
});
await Case("interrupted-new-launcher-recovers-from-copy", async () => {
    var root = Prepare("new-launcher-interrupted"); var before = DataHashes(root); var work = Path.Combine(root, ".update"); Directory.CreateDirectory(work);
    var oldLauncher = UpdatePackage.Hash(Path.Combine(root, "DesktopTimetable.exe"));
    File.WriteAllText(Path.Combine(work, "transaction.flag"), "1.1.1");
    Directory.Move(Path.Combine(root, "app"), Path.Combine(work, "old-app"));
    File.Copy(Path.Combine(root, "DesktopTimetable.exe"), Path.Combine(work, "old-launcher.exe"));
    File.Copy(Path.Combine(lab, "target", "DesktopTimetable.exe"), Path.Combine(root, "DesktopTimetable.exe"), true);
    Directory.CreateDirectory(Path.Combine(root, "app"));
    using var process = Process.Start(new ProcessStartInfo(Path.Combine(root, "DesktopTimetable.exe")) { UseShellExecute = false, WorkingDirectory = root,
        ArgumentList = { "--smoke-test", "--data-dir", root } })!;
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await process.WaitForExitAsync(limit.Token);
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < deadline && (!File.Exists(Path.Combine(root, "user-data", "startup.log"))
        || !File.ReadAllText(Path.Combine(root, "user-data", "startup.log")).Contains("STARTUP CHECK PASSED"))) await Task.Delay(200);
    Check(VersionAt(root) == "1.1.0" && UpdatePackage.Hash(Path.Combine(root, "DesktopTimetable.exe")) == oldLauncher,
        "Recovery copy failed to restore the running new launcher"); Unchanged(root, before);
});
await Case("waits-for-running-old-app-before-install", async () => {
    var root = Prepare("running-parent"); var stage = await Download("newer", root); var before = DataHashes(root);
    using var parent = Process.Start(new ProcessStartInfo(Path.Combine(root, "app", "DesktopTimetable.exe")) { UseShellExecute = false, WorkingDirectory = root,
        ArgumentList = { "--data-dir", root, "--windowed", "--update-test-url", service.ToString() } })!;
    try
    {
        await Task.Delay(1200);
        Check(!parent.HasExited, "Old application failed to launch");
        var installation = Apply(root, stage, parent);
        await Task.Delay(1000);
        Check(VersionAt(root) == "1.1.0" && !installation.IsCompleted, "Updater replaced an application which was still running");
        // Only terminate the disposable process started by this fixture. The
        // normal UI uses a graceful save/exit before handing off the updater.
        parent.Kill(); await parent.WaitForExitAsync();
        Check(await installation == 0 && VersionAt(root) == "1.1.1", "Update did not proceed after old app exited");
        Unchanged(root, before);
    }
    finally { if (!parent.HasExited) parent.Kill(); }
});
await Case("offline-service", async () => { using var offline = new UpdateClient(new Uri("http://127.0.0.1:1/"), TimeSpan.FromSeconds(1));
    await Reject(async () => { await offline.CheckAsync("1.1.0", default); }, "Offline service reported up-to-date"); });
var formal = Path.Combine(Directory.GetParent(lab)!.FullName, "release", "app", "DesktopTimetable.exe");
var projectRoot = Directory.GetParent(lab)!.FullName;
var formalPackage = Path.Combine(projectRoot, "updatepack", "1.2.0", UpdateClient.AssetName);
if (File.Exists(formalPackage))
    await Case("formal-v1.2.0-package-installs-preserves-data", async () =>
    {
        var root = Prepare("formal-package"); var before = DataHashes(root);
        var digest = File.ReadAllText(formalPackage + ".sha256").Trim().Split(' ')[0];
        Check(UpdatePackage.Hash(formalPackage).Equals(digest, StringComparison.OrdinalIgnoreCase), "Formal package checksum differs");
        var stage = Path.Combine(root, ".update", "download-" + Guid.NewGuid().ToString("N"), "stage");
        ZipFile.ExtractToDirectory(formalPackage, stage);
        UpdatePackage.Validate(stage, "1.2.0");
        var manifest = UpdatePackage.ReadXml<UpdateManifest>(Path.Combine(stage, "update-manifest.xml"));
        foreach (var file in manifest.Files)
            Check(UpdatePackage.Hash(Path.Combine(projectRoot, "release", file.Path.Replace('/', Path.DirectorySeparatorChar))) == file.Sha256,
                "Update differs from portable program: " + file.Path);
        Check(await Apply(root, stage, version: "1.2.0") == 0 && VersionAt(root) == "1.2.0", "Formal package did not install and restart");
        Unchanged(root, before);
    });
if (File.Exists(formal))
    foreach (var (scenario, remote) in new[] { ("current-v1.2", "1.2.0"), ("future-v1.2", "1.2.1"), ("no-release", "none") })
        await Case("real-update-window-" + scenario, async () =>
        {
            await Scenario(scenario); var root = Prepare("update-ui"); var before = DataHashes(root);
            using var process = Process.Start(new ProcessStartInfo(formal) { UseShellExecute = false, WorkingDirectory = root,
                ArgumentList = { "--smoke-test", "--data-dir", root, "--update-test-url", service.ToString(), "--update-ui-remote", remote } })!;
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25)); await process.WaitForExitAsync(limit.Token);
            Check(process.ExitCode == 0, "Real update window failed UI validation"); Unchanged(root, before);
        });
await Scenario("newer");
Directory.CreateDirectory(Path.Combine(lab, "results"));
File.WriteAllText(Path.Combine(lab, "results", "checks.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} update cases passed");
if (failures == 0)
{
    await Task.Delay(1500);
    foreach (var folder in Directory.GetDirectories(cases))
    {
        UpdatePackage.CheckLinks(folder);
        try { Directory.Delete(folder, true); }
        catch (IOException) { Console.WriteLine("Temporary case still in use: " + Path.GetFileName(folder)); }
        catch (UnauthorizedAccessException) { Console.WriteLine("Temporary case needs later cleanup: " + Path.GetFileName(folder)); }
    }
}
return failures == 0 ? 0 : 1;

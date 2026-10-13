using Microsoft.UI.Xaml;

namespace Timetable.Desktop;

public partial class App : Application
{
    private MainWindow? window;
    private static Mutex? instanceMutex;
    public App()
    {
        StartupLog.Write("App constructor");
        DebugSettings.IsXamlResourceReferenceTracingEnabled = StartupLog.Diagnostic;
        DebugSettings.XamlResourceReferenceFailed += (_, args) => StartupLog.Write("XAML resource: " + args.Message);
        UnhandledException += (_, args) =>
        {
            StartupLog.Fail(args.Exception);
            args.Handled = true;
            Exit();
        };
        InitializeComponent();
        StartupLog.Write("Application resources initialized");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupLog.Write("OnLaunched");
        try
        {
        var commandLine = Environment.GetCommandLineArgs();
        var smokeTest = commandLine.Contains("--smoke-test");
        var interactionTest = commandLine.Contains("--interaction-test");
        if (!smokeTest && !interactionTest && !commandLine.Contains("--layer-test"))
        {
            var testIndex = Array.IndexOf(commandLine, "--update-test-url");
            var suffix = testIndex >= 0 ? ".UpdateLab." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())))[..16] : "";
            instanceMutex = new Mutex(true, "Local\\DesktopTimetable.CSharp" + suffix, out var firstInstance);
            if (!firstInstance) { instanceMutex.Dispose(); Exit(); return; }
        }
        var index = Array.IndexOf(commandLine, "--data-dir");
        var directory = index >= 0 && index + 1 < commandLine.Length
            ? Path.GetFullPath(commandLine[index + 1]) : FindDataDirectory();
        window = new MainWindow(directory);
        StartupLog.Write("MainWindow constructed");
        window.Activate();
        StartupLog.Write("MainWindow activated");
        window.AttachToDesktop();
        StartupLog.Write("Desktop attached");
        var healthIndex = Array.IndexOf(commandLine, "--update-health");
        if (healthIndex >= 0 && healthIndex + 1 < commandLine.Length)
        {
            await Task.Delay(500);
            window.WriteUpdateHealth(commandLine[healthIndex + 1]);
        }
        if (smokeTest || interactionTest)
        {
            if (interactionTest) await window.CheckInteractionsAsync();
            else await window.CheckStartupAsync();
            StartupLog.Write("STARTUP CHECK PASSED");
            Exit();
        }
        }
        catch (Exception error)
        {
            StartupLog.Fail(error);
            Exit();
        }
    }

    private static string FindDataDirectory()
    {
        var executableDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        // A release has one app subfolder and keeps all editable data in its parent.
        if (executableDirectory.Name.Equals("app", StringComparison.OrdinalIgnoreCase) && executableDirectory.Parent != null)
            return executableDirectory.Parent.FullName;
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "timetable.json"))) return AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(Environment.CurrentDirectory, "winui-app", "Timetable.Desktop.csproj")))
            return Environment.CurrentDirectory;
        return AppContext.BaseDirectory;
    }
}

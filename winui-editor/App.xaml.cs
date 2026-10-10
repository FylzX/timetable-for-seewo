using Microsoft.UI.Xaml;

namespace Timetable.Editor;

public partial class App : Application
{
    private MainWindow? window;
    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(commandLine, "--data-dir");
        var directory = index >= 0 && index + 1 < commandLine.Length
            ? Path.GetFullPath(commandLine[index + 1]) : FindDataDirectory();
        window = new MainWindow(directory, commandLine.Contains("--bridge"));
        window.Activate();
    }

    private static string FindDataDirectory()
    {
        // Portable releases always prefer data beside their executable.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "timetable.json")))
            return AppContext.BaseDirectory;
        // Allows dotnet run / Visual Studio debugging from this project.
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "timetable.json")))
                    return directory.FullName;
        return Environment.CurrentDirectory;
    }
}

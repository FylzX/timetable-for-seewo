using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Timetable.Updates;
using Windows.Graphics;
using WinRT.Interop;

namespace Timetable.Desktop;

internal sealed class UpdateWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly TextBlock result = new() { Text = "正在检查更新…", FontSize = 22, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock versions = new() { Text = "本机版本：v" + UpdateClient.CurrentVersion, FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock notes = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Visibility = Visibility.Visible };
    private readonly Button install = new() { Content = "下载并更新", Visibility = Visibility.Collapsed };
    private readonly Button close = new() { Content = "关闭" };
    private readonly UpdateClient client;
    private readonly Uri? testBase;
    private readonly Func<Task<bool>> prepareExit;
    private readonly Action exit;
    private AvailableUpdate? available;
    private bool busy, handedOff;
    private string? stage;
    private readonly Task checkTask;

    internal UpdateWindow(Uri? testBase, Func<Task<bool>> prepareExit, Action exit)
    {
        this.testBase = testBase; this.prepareExit = prepareExit; this.exit = exit;
        client = new UpdateClient(testBase);
        Title = "检查更新";
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
        var body = new StackPanel { Spacing = 18, Padding = new Thickness(24) };
        body.Children.Add(result); body.Children.Add(versions);
        if (testBase != null) body.Children.Add(new TextBlock { Text = "更新测试版 · 使用本机模拟服务", Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"] });
        body.Children.Add(new ScrollViewer { Content = notes, MaxHeight = 160, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        body.Children.Add(progress);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12 };
        install.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        install.Click += async (_, _) => await InstallAsync();
        close.Click += (_, _) => Close();
        actions.Children.Add(close); actions.Children.Add(install); body.Children.Add(actions);
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Closed += (_, _) => { cancellation.Cancel(); if (!busy) client.Dispose(); };
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = NativeWidget.Dpi(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd), DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(540 * scale), area.Width - 32);
        var height = Math.Min((int)(420 * scale), area.Height - 32);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsMaximizable = false;
        checkTask = CheckAsync();
    }

    internal async Task CheckPresentationAsync(string expectedRemote)
    {
        await checkTask.WaitAsync(TimeSpan.FromSeconds(10));
        var expected = expectedRemote == "none" ? "暂无正式 Release" : "v" + expectedRemote;
        if (Content is not FrameworkElement root || root.XamlRoot == null || root.ActualWidth <= 0
            || !versions.Text.Contains("本机版本：v" + UpdateClient.CurrentVersion) || !versions.Text.Contains("远程版本：" + expected))
            throw new InvalidOperationException("更新窗口未显示正确的本机和远程版本。");
        var newer = expectedRemote != "none" && UpdateClient.ParseVersion(expectedRemote) > UpdateClient.ParseVersion(UpdateClient.CurrentVersion);
        if (result.Text != (newer ? "发现新版本" : expectedRemote == "none" ? "尚未发布正式版本" : "当前已是最新版本")
            || (install.Visibility == Visibility.Visible) != newer)
            throw new InvalidOperationException("更新窗口状态或下载按钮不正确。");
    }

    private async Task CheckAsync()
    {
        busy = true;
        try
        {
            var check = await client.CheckAsync(UpdateClient.CurrentVersion, cancellation.Token);
            versions.Text = $"本机版本：v{check.Current}\n远程版本：{(check.Remote == null ? "暂无正式 Release" : "v" + check.Remote)}";
            available = check.Update;
            result.Text = available != null ? "发现新版本" : check.Remote == null ? "尚未发布正式版本" : "当前已是最新版本";
            if (available != null) { notes.Text = String.IsNullOrWhiteSpace(available.Notes) ? "此版本未提供更新说明。" : available.Notes; install.Visibility = Visibility.Visible; }
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        { result.Text = "检查更新失败"; notes.Text = error.Message + "\n请检查网络后重试。"; }
        catch (Exception) when (cancellation.IsCancellationRequested) { }
        finally
        {
            busy = false; progress.Visibility = Visibility.Collapsed;
            if (cancellation.IsCancellationRequested) client.Dispose();
        }
    }

    private async Task InstallAsync()
    {
        if (busy || available == null) return;
        busy = true; install.IsEnabled = false; close.Content = "取消下载";
        var location = new DirectoryInfo(AppContext.BaseDirectory);
        var root = location.Name.Equals("app", StringComparison.OrdinalIgnoreCase) ? location.Parent!.FullName : "";
        try
        {
            if (root == "" || !File.Exists(Path.Combine(root, "DesktopTimetable.exe")))
                throw new InvalidOperationException("请在便携发行版中安装更新，源码预览窗口仅支持检查版本。");
            result.Text = "正在下载更新"; progress.Visibility = Visibility.Visible; progress.IsIndeterminate = false;
            stage = await client.DownloadAsync(available, root, new Progress<double>(value => progress.Value = value), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            result.Text = "下载和校验已完成";
            if (!await prepareExit()) { result.Text = "更新已取消"; return; }
            cancellation.Token.ThrowIfCancellationRequested();
            StartInstaller(root, stage, available.Version, testBase?.ToString() ?? "", false);
            handedOff = true;
            Close(); exit();
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        { result.Text = "更新未完成"; notes.Text = error.Message + "\n当前程序仍可使用。"; }
        catch (Exception) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (!handedOff && stage != null)
            {
                try { Directory.Delete(Path.GetDirectoryName(stage)!, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            busy = false; install.IsEnabled = true; close.Content = "关闭"; progress.Visibility = Visibility.Collapsed;
            if (cancellation.IsCancellationRequested) client.Dispose();
        }
    }

    internal static Process StartInstaller(string root, string stage, string version, string testUrl, bool smokeTest)
    {
        var work = Path.Combine(root, ".update");
        UpdatePackage.CheckLinks(work);
        var updater = Path.Combine(work, "Updater.exe");
        File.Copy(Path.Combine(root, "DesktopTimetable.exe"), updater, true);
        using var current = Process.GetCurrentProcess();
        UpdatePackage.WriteXml(Path.Combine(work, "pending.xml"), new UpdateRequest { Version = version, Stage = stage,
            ParentPid = smokeTest ? 0 : Environment.ProcessId, ParentStartTicks = current.StartTime.ToUniversalTime().Ticks,
            TestUrl = testUrl, SmokeTest = smokeTest });
        return Process.Start(new ProcessStartInfo(updater) { UseShellExecute = false, WorkingDirectory = root,
            ArgumentList = { "--apply-update", root } })!;
    }
}

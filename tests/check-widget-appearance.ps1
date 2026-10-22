# Captures only our widget over a synthetic background, never the whole desktop.
param([string]$ReleaseDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $ReleaseDirectory) {
    $version = [string](([xml](Get-Content -LiteralPath (Join-Path $root 'winui-app/Timetable.Desktop.csproj') -Raw)).Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1 -ExpandProperty Version)
    $ReleaseDirectory = Join-Path $root ('release/' + $version)
}
$output = Join-Path $root 'test-output/widget-appearance'
$data = Join-Path $output 'data'
New-Item -ItemType Directory -Force -Path $data | Out-Null
foreach ($name in @('timetable.json','exceptions.json','template.json')) {
    Copy-Item -LiteralPath (Join-Path $ReleaseDirectory $name) -Destination (Join-Path $data $name)
}
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class WidgetCapture
{
    public delegate bool EnumProc(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public static IntPtr Find(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr data) {
            uint pid; GetWindowThreadProcessId(window, out pid);
            if (pid == processId && IsWindowVisible(window)) { found = window; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static Bitmap Capture(IntPtr window)
    {
        Rect rect; GetWindowRect(window, out rect);
        var bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
        return bitmap;
    }
}
'@
[WidgetCapture]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
[WidgetCapture]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
$child = $null
$backdrop = New-Object System.Windows.Forms.Form
$backdrop.FormBorderStyle = 'None'
$backdrop.AutoScaleMode = 'None'
$backdrop.ShowInTaskbar = $false
$backdrop.TopMost = $true
$backdrop.BackColor = [Drawing.Color]::FromArgb(38,156,210)
function Pump([int]$Milliseconds) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $Milliseconds) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 25
    }
}
try {
    $launcher = Start-Process -FilePath (Join-Path $ReleaseDirectory 'DesktopTimetable.exe') -ArgumentList ('--windowed --layer-test --diagnostic --data-dir "'+$data+'"') -WindowStyle Hidden -PassThru
    Pump 1800
    $info = Get-CimInstance Win32_Process -Filter "Name='DesktopTimetable.exe'" |
        Where-Object { $_.ParentProcessId -eq $launcher.Id -and $_.ExecutablePath -eq [IO.Path]::GetFullPath((Join-Path $ReleaseDirectory 'app/DesktopTimetable.exe')) } |
        Select-Object -First 1
    if ($null -eq $info) { throw 'Diagnostic widget did not start. Exit the existing C# widget first.' }
    $child = Get-Process -Id $info.ProcessId
    $hwnd = [WidgetCapture]::Find($child.Id)
    if ($hwnd -eq [IntPtr]::Zero) { throw 'Widget window is missing.' }
    $scale = [WidgetCapture]::GetDpiForWindow($hwnd) / 96.0
    $screen = [Windows.Forms.Screen]::FromHandle($hwnd).WorkingArea
    $x = $screen.X + 40; $y = $screen.Y + 40
    $height = [Math]::Min([int](632 * $scale), $screen.Height - 80)
    $width = [int](380 * $scale)
    $backdrop.Bounds = New-Object Drawing.Rectangle ($x-4),($y-4),($width+8),($height+8)
    $backdrop.Show()
    [WidgetCapture]::SetWindowPos($backdrop.Handle,[IntPtr](-1),($x-4),($y-4),($width+8),($height+8),0x10) | Out-Null
    [WidgetCapture]::SetWindowPos($hwnd,[IntPtr](-1),$x,$y,$width,$height,0x10) | Out-Null
    # Force a deterministic empty day in the disposable data directory.
    $dateKey = (Get-Date).ToString('yyyy-MM-dd')
    [IO.File]::WriteAllText((Join-Path $data 'exceptions.json'), ('{"'+$dateKey+'":[]}'))
    Pump 1500
    $blue = [WidgetCapture]::Capture($hwnd)
    $blue.Save((Join-Path $output 'empty.png'),[Drawing.Imaging.ImageFormat]::Png)
    $sampleBlue = $blue.GetPixel([int]($width/2),[int]($height*.75))
    $corner = $blue.GetPixel(1,1)
    $blue.Dispose()
    $backdrop.BackColor = [Drawing.Color]::FromArgb(220,90,55)
    Pump 500
    $red = [WidgetCapture]::Capture($hwnd)
    $sampleRed = $red.GetPixel([int]($width/2),[int]($height*.75))
    $red.Dispose()
    if ($sampleBlue.ToArgb() -eq $sampleRed.ToArgb()) { throw 'Panel is opaque: background color does not show through.' }
    if ($corner.B -lt 180 -or $corner.R -gt 80) { throw 'Rounded corner has a visible system frame.' }
    if (([WidgetCapture]::GetWindowLongPtr($hwnd,-16).ToInt64() -band 0x00C40000) -ne 0) { throw 'Native caption or resize frame is still enabled.' }
    $backdrop.BackColor = [Drawing.Color]::FromArgb(38,156,210)
    $weekly = Get-Content -LiteralPath (Join-Path $data 'timetable.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $exception = @{}; $exception[$dateKey] = @($weekly.'1')
    [IO.File]::WriteAllText((Join-Path $data 'exceptions.json'), (ConvertTo-Json -InputObject $exception -Depth 8))
    Pump 1400
    $classes = [WidgetCapture]::Capture($hwnd)
    $classes.Save((Join-Path $output 'classes.png'),[Drawing.Imaging.ImageFormat]::Png)
    $classes.Dispose()
    $tallHeight = [Math]::Min([int](970*$scale), $screen.Height - 80)
    $backdrop.Bounds = New-Object Drawing.Rectangle ($x-4),($y-4),($width+8),($tallHeight+8)
    [WidgetCapture]::SetWindowPos($backdrop.Handle,[IntPtr](-1),($x-4),($y-4),($width+8),($tallHeight+8),0x10) | Out-Null
    [WidgetCapture]::SetWindowPos($hwnd,[IntPtr](-1),$x,$y,$width,$tallHeight,0x10) | Out-Null
    Pump 600
    $tall = [WidgetCapture]::Capture($hwnd)
    $tall.Save((Join-Path $output 'tall.png'),[Drawing.Imaging.ImageFormat]::Png)
    $tall.Dispose()
    Write-Host ('Transparency and frame checks passed. Screenshots: '+$output)
    Write-Host ('Panel samples: blue='+$sampleBlue+' red='+$sampleRed+' corner='+$corner)
}
finally {
    if ($null -ne $child -and -not $child.HasExited) {
        [WidgetCapture]::PostMessage([WidgetCapture]::Find($child.Id),0x10,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
        if (-not $child.WaitForExit(3000)) { Stop-Process -Id $child.Id }
    }
    $backdrop.Close(); $backdrop.Dispose()
}

param([string]$ReleaseDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public static class DesktopLayerCheck
{
    public delegate bool EnumProc(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flag);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
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
}
'@
[DesktopLayerCheck]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
function Pump([int]$Milliseconds) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $Milliseconds) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 25 }
}
$child = $null; $overlay = $null; $desktopToggled = $false; $shell = $null
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ReleaseDirectory) { $ReleaseDirectory = Join-Path $projectRoot 'release/latest' }
$testRoot = Join-Path $projectRoot 'test-output'
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$data = Join-Path $testRoot ('desktop-layer-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data | Out-Null
try {
    # Use a data copy so closing the test window does not change real settings.
    foreach ($name in @('timetable.json', 'exceptions.json', 'template.json')) {
        Copy-Item -LiteralPath (Join-Path $ReleaseDirectory $name) -Destination (Join-Path $data $name)
    }
    $launcher = Start-Process -FilePath (Join-Path $ReleaseDirectory 'app/DesktopTimetable.exe') -ArgumentList ('--layer-test --diagnostic --data-dir "' + $data + '"') -WindowStyle Hidden -PassThru
    $child = $launcher
    Pump 1500
    $hwnd = [DesktopLayerCheck]::Find($child.Id)
    if ($hwnd -eq [IntPtr]::Zero) { throw 'Diagnostic widget did not start.' }
    if (([DesktopLayerCheck]::GetWindowLongPtr($hwnd,-20).ToInt64() -band 0x08000000) -eq 0) { throw 'Widget can activate.' }
    if ([DesktopLayerCheck]::SendMessage($hwnd,0x21,[IntPtr]::Zero,[IntPtr]::Zero).ToInt64() -ne 3) { throw 'Mouse activation is allowed.' }
    [DesktopLayerCheck]::SetWindowPos($hwnd,[IntPtr](-1),0,0,0,0,0x13) | Out-Null
    if (([DesktopLayerCheck]::GetWindowLongPtr($hwnd,-20).ToInt64() -band 8) -ne 0) { throw 'Widget became topmost.' }
    $rect = New-Object DesktopLayerCheck+Rect
    [DesktopLayerCheck]::GetWindowRect($hwnd,[ref]$rect) | Out-Null
    $overlay = New-Object Windows.Forms.Form
    $overlay.AutoScaleMode = 'None'; $overlay.Text = 'Desktop layer test'; $overlay.BackColor = [Drawing.Color]::SteelBlue
    $overlay.Bounds = New-Object Drawing.Rectangle $rect.Left,$rect.Top,($rect.Right-$rect.Left),($rect.Bottom-$rect.Top)
    $overlay.Show(); $overlay.Activate(); Pump 500
    [DesktopLayerCheck]::SetWindowPos($overlay.Handle,[IntPtr]::Zero,$rect.Left,$rect.Top,($rect.Right-$rect.Left),($rect.Bottom-$rect.Top),0) | Out-Null
    [DesktopLayerCheck]::SetWindowPos($hwnd,[IntPtr]::Zero,0,0,0,0,3) | Out-Null
    [DesktopLayerCheck]::ShowWindow($hwnd,9) | Out-Null
    [DesktopLayerCheck]::SetForegroundWindow($hwnd) | Out-Null
    Pump 200
    $point = New-Object Drawing.Point ([int](($rect.Left+$rect.Right)/2)),([int](($rect.Top+$rect.Bottom)/2))
    $hit = [DesktopLayerCheck]::GetAncestor([DesktopLayerCheck]::WindowFromPoint($point),2)
    if ($hit -ne $overlay.Handle) { throw ('Normal window is not above widget. Hit='+$hit+' widget='+$hwnd+' overlay='+$overlay.Handle) }
    $shell = New-Object -ComObject Shell.Application
    $shell.ToggleDesktop(); $desktopToggled = $true; Pump 700
    if (-not [DesktopLayerCheck]::IsWindowVisible($hwnd) -or [DesktopLayerCheck]::IsIconic($hwnd)) { throw 'Show Desktop hid or minimized the widget.' }
    $shell.ToggleDesktop(); $desktopToggled = $false; Pump 700
    if ([DesktopLayerCheck]::GetAncestor([DesktopLayerCheck]::WindowFromPoint($point),2) -ne $overlay.Handle) { throw 'Widget covered a restored normal window.' }
    [DesktopLayerCheck]::SendMessage($hwnd,0x112,[IntPtr](0xf020),[IntPtr]::Zero) | Out-Null
    if ([DesktopLayerCheck]::IsIconic($hwnd)) { throw 'Desktop widget accepted minimization.' }
    Write-Host 'Desktop layer checks passed: no click activation, no topmost promotion, normal windows remain above it, Show Desktop preserves visibility.'
}
finally {
    if ($desktopToggled -and $null -ne $shell) { $shell.ToggleDesktop() }
    if ($null -ne $overlay) { $overlay.Close(); $overlay.Dispose() }
    if ($null -ne $child -and -not $child.HasExited) {
        [DesktopLayerCheck]::PostMessage([DesktopLayerCheck]::Find($child.Id),0x10,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
        if (-not $child.WaitForExit(3000)) { Stop-Process -Id $child.Id }
    }
    $resolved = [IO.Path]::GetFullPath($data)
    $allowed = [IO.Path]::GetFullPath($testRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test data is outside the test output directory.' }
    $item = Get-Item -LiteralPath $resolved -Force -ErrorAction SilentlyContinue
    if ($null -ne $item -and -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

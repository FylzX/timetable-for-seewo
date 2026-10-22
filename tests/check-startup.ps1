param([string]$ReleaseDirectory = '', [switch]$Interactions, [switch]$EditorOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseVersion = [string](([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'winui-app/Timetable.Desktop.csproj') -Raw)).Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1 -ExpandProperty Version)
$releaseRoot = if ($ReleaseDirectory) { [IO.Path]::GetFullPath($ReleaseDirectory) } else { Join-Path $projectRoot ('release/' + $releaseVersion) }
$executable = Join-Path $releaseRoot 'DesktopTimetable.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run build-portable.ps1 first.' }
$testRoot = Join-Path $projectRoot 'test-output'
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$data = Join-Path $testRoot ('startup-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data | Out-Null
try {
    $before = @{}
    foreach ($name in @('timetable.json', 'exceptions.json', 'template.json')) {
        $source = Join-Path $releaseRoot $name
        Copy-Item -LiteralPath $source -Destination (Join-Path $data $name)
        $before[$name] = (Get-FileHash -LiteralPath $source).Hash
    }
    $mode = if ($EditorOnly) { '--interaction-test' } elseif ($Interactions) { '--interaction-test --windowed' } else { '--smoke-test' }
    $process = Start-Process -FilePath $executable -ArgumentList ($mode + ' --data-dir "' + $data + '"') -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) {
        # Only stop the process tree started by this check.
        Get-CimInstance Win32_Process -Filter "Name='DesktopTimetable.exe'" |
            Where-Object { $_.ParentProcessId -eq $process.Id -and $_.ExecutablePath -eq (Join-Path $releaseRoot 'app/DesktopTimetable.exe') } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        throw ('Startup check timed out. See ' + (Join-Path $releaseRoot 'user-data/startup.log'))
    }
    if ($process.ExitCode -ne 0) { throw ('Startup check failed: ' + $process.ExitCode + '. See ' + (Join-Path $releaseRoot 'user-data/startup.log')) }
    foreach ($name in $before.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $data $name)).Hash -ne $before[$name] -or
            (Get-FileHash -LiteralPath (Join-Path $releaseRoot $name)).Hash -ne $before[$name]) {
            throw ('Startup check changed schedule data: ' + $name)
        }
    }
    if ($EditorOnly) { Write-Host 'Time input, deletion scroll preservation and appearance preview/save/discard checks passed. Schedule files unchanged.' }
    elseif ($Interactions) { Write-Host 'Resize cursors, time input, deletion scroll preservation and appearance preview/save/discard checks passed. Schedule files unchanged.' }
    else { Write-Host 'Published launcher, main window, all three editor tabs and test clock started successfully. Schedule files unchanged.' }
}
finally {
    $resolved = [IO.Path]::GetFullPath($data)
    $allowed = [IO.Path]::GetFullPath($testRoot) + [IO.Path]::DirectorySeparatorChar
    if ($resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
        $item = Get-Item -LiteralPath $resolved -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}

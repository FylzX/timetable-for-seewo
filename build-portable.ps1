param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'release/latest')
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$appProject = Join-Path $projectRoot 'winui-app/Timetable.Desktop.csproj'
if (-not (Test-Path -LiteralPath $appProject)) { throw 'C# main project is missing.' }
[xml]$projectFile = Get-Content -LiteralPath $appProject -Raw
$configuredVersion = [string]($projectFile.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1 -ExpandProperty Version)
if ($configuredVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Project version must use three numeric parts.' }
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'release'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output -ne $releaseRoot -and -not $output.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must be release or one of its subdirectories.'
}

# Only replace generated application files. Keep edited schedules and settings.
New-Item -ItemType Directory -Force -Path $output | Out-Null
$stage = Join-Path $output ('.build-' + [Guid]::NewGuid().ToString('N'))
$appOutput = Join-Path $output 'app'
New-Item -ItemType Directory -Path $stage | Out-Null
function Remove-GeneratedDirectory([string]$Target) {
    $resolved = [IO.Path]::GetFullPath($Target)
    if (-not $resolved.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a directory outside the release output.'
    }
    if (Test-Path -LiteralPath $resolved) {
        $item = Get-Item -LiteralPath $resolved -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to remove a linked directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-local'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
try {
    $publishArgs = @(
        'publish', $appProject, '--configuration', 'Release', '--runtime', 'win-x64',
        '--self-contained', 'true', '-p:WindowsAppSDKSelfContained=true',
        ('-p:RestorePackagesPath=' + (Join-Path $projectRoot '.dev-packages')),
        '-p:NuGetAudit=false', '--output', $stage, '--nologo'
    )
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "C# publish failed: $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'DesktopTimetable.exe'))) { throw 'Main executable is missing.' }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'resources.pri'))) { throw 'Application XAML resources are missing.' }
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework compiler is missing.' }
    $launcherStage = Join-Path $stage 'PortableLauncher.exe'
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage 'DesktopTimetable.dll'))
    $version = '{0}.{1}.{2}' -f $versionInfo.FileMajorPart,$versionInfo.FileMinorPart,$versionInfo.FileBuildPart
    if ($version -ne $configuredVersion) { throw 'Published application version differs from the project version.' }
    $launcherVersion = Join-Path $stage 'LauncherVersion.cs'
    [IO.File]::WriteAllText($launcherVersion, ('[assembly: System.Reflection.AssemblyVersion("' + $version + '.0")][assembly: System.Reflection.AssemblyFileVersion("' + $version + '.0")]'))
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll ('/win32icon:' + (Join-Path $projectRoot 'winui-app/Assets/calendar.ico')) ('/out:' + $launcherStage) (Join-Path $projectRoot 'winui-app/PortableLauncher.cs') (Join-Path $projectRoot 'winui-app/PortableUpdateInstaller.cs') (Join-Path $projectRoot 'winui-app/UpdatePackage.cs') $launcherVersion
    if ($LASTEXITCODE -ne 0) { throw "Launcher build failed: $LASTEXITCODE" }
    Remove-Item -LiteralPath $launcherVersion -Force
    Move-Item -LiteralPath $launcherStage -Destination (Join-Path $output 'DesktopTimetable.exe') -Force
    Remove-GeneratedDirectory $appOutput
    Move-Item -LiteralPath $stage -Destination $appOutput
    # Remove files from the earlier two-runtime prototype, using the new publish
    # manifest as the allowlist. Editable JSON and user-data are never in this list.
    foreach ($artifact in (Get-ChildItem -LiteralPath $appOutput -Force)) {
        $previous = Join-Path $output $artifact.Name
        if ($artifact.Name -ne 'DesktopTimetable.exe' -and (Test-Path -LiteralPath $previous)) {
            if ($artifact.PSIsContainer) { Remove-GeneratedDirectory $previous }
            else { Remove-Item -LiteralPath $previous -Force }
        }
    }
    Remove-GeneratedDirectory (Join-Path $output 'editor')
    foreach ($name in @('timetable.json', 'exceptions.json', 'template.json')) {
        $destination = Join-Path $output $name
        if (-not (Test-Path -LiteralPath $destination)) {
            $source = Join-Path $projectRoot $name
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $destination }
        }
    }
    $previousLauncher = Join-Path $output 'Start.cmd'
    if (Test-Path -LiteralPath $previousLauncher) { Remove-Item -LiteralPath $previousLauncher -Force }
    $info = "Double-click DesktopTimetable.exe to open the C# WinUI timetable.`r`nMove the whole folder, including app and JSON files. The root EXE is a small launcher.`r`nPython and .NET installation are not required on the target PC.`r`nRequires Windows 10 1809+ or Windows 11, x64. Windows 11 recommended.`r`nSettings and startup.log are saved in user-data beside these JSON files.`r`nRebuilding preserves your edited JSON files and user-data.`r`n"
    [IO.File]::WriteAllText((Join-Path $output 'Portable.txt'), $info, [Text.Encoding]::UTF8)
    . (Join-Path $projectRoot 'tools/package-update.ps1')
    New-UpdatePackage $output (Join-Path $projectRoot ('updatepack/' + $version)) $version
    # Remove the outputs of the previous packaging layout from the portable app.
    Remove-GeneratedDirectory (Join-Path $output 'packages')
    $previousManifest = Join-Path $output 'update-manifest.xml'
    if (Test-Path -LiteralPath $previousManifest) { Remove-Item -LiteralPath $previousManifest -Force }
    Write-Host "Portable folder: $output"
    Write-Host 'The editor is included in the main C# app. No installer was generated.'
}
finally { Remove-GeneratedDirectory $stage }

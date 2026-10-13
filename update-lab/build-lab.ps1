param([switch]$UseCurrentSources)
$ErrorActionPreference = 'Stop'
$lab = $PSScriptRoot
$projectRoot = Split-Path -Parent $lab
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-local'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$prototype = Join-Path $lab 'prototype'
if ($UseCurrentSources -or -not (Test-Path -LiteralPath (Join-Path $prototype 'winui-app/Timetable.Desktop.csproj'))) {
    foreach ($folder in @('winui-app','winui-editor')) {
        $source = Join-Path $projectRoot $folder
        $target = Join-Path $prototype $folder
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in @('.cs','.xaml','.csproj','.manifest','.targets','.Config') } |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $target $_.Name) -Force }
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $prototype 'winui-app/Assets') | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'winui-app/Assets/calendar.ico') -Destination (Join-Path $prototype 'winui-app/Assets/calendar.ico') -Force
}
foreach ($folder in @('packages','server','results','build-temp')) { New-Item -ItemType Directory -Force -Path (Join-Path $lab $folder) | Out-Null }
function Write-Manifest([string]$Folder, [string]$Version) {
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create((Join-Path $Folder 'update-manifest.xml'), $settings)
    try {
        $writer.WriteStartElement('UpdateManifest')
        $writer.WriteElementString('Format','1'); $writer.WriteElementString('Version',$Version)
        $writer.WriteStartElement('Files')
        $files = @(Get-Item -LiteralPath (Join-Path $Folder 'DesktopTimetable.exe')) + @(Get-ChildItem -LiteralPath (Join-Path $Folder 'app') -Recurse -File)
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($Folder.Length + 1).Replace('\','/')
            $writer.WriteStartElement('UpdateFile')
            $writer.WriteElementString('Path',$relative)
            $writer.WriteElementString('Sha256',(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())
            $writer.WriteElementString('Size',$file.Length.ToString([Globalization.CultureInfo]::InvariantCulture))
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement(); $writer.WriteEndElement()
    } finally { $writer.Dispose() }
}
function Write-Zip([string]$Folder, [string]$ArchivePath) {
    if (Test-Path -LiteralPath $ArchivePath) { Remove-Item -LiteralPath $ArchivePath -Force }
    $zip = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $Folder -Recurse -File) {
            $relative = $file.FullName.Substring($Folder.Length + 1).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
}
foreach ($version in @('1.1.0','1.1.1')) {
    $output = Join-Path $lab $(if ($version -eq '1.1.0') { 'v1.1.0' } else { 'target' })
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    & dotnet publish (Join-Path $prototype 'winui-app/Timetable.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true ('-p:Version=' + $version) ('-p:RestorePackagesPath=' + (Join-Path $projectRoot '.dev-packages')) -p:NuGetAudit=false -o (Join-Path $output 'app') --nologo
    if ($LASTEXITCODE -ne 0) { throw "Test app build failed: $version" }
    $versionSource = Join-Path $lab 'build-temp/LauncherVersion.cs'
    [IO.File]::WriteAllText($versionSource, ('[assembly: System.Reflection.AssemblyVersion("' + $version + '.0")][assembly: System.Reflection.AssemblyFileVersion("' + $version + '.0")]'))
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll ('/win32icon:' + (Join-Path $prototype 'winui-app/Assets/calendar.ico')) ('/out:' + (Join-Path $output 'DesktopTimetable.exe')) (Join-Path $prototype 'winui-app/PortableLauncher.cs') (Join-Path $prototype 'winui-app/PortableUpdateInstaller.cs') (Join-Path $prototype 'winui-app/UpdatePackage.cs') $versionSource
    if ($LASTEXITCODE -ne 0) { throw 'Test launcher build failed' }
    Write-Manifest $output $version
    if ($version -eq '1.1.0') {
        foreach ($name in @('timetable.json','exceptions.json','template.json')) {
            $destination = Join-Path $output $name
            if (-not (Test-Path -LiteralPath $destination)) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $destination }
        }
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$newZip = Join-Path $lab 'packages/new.zip'
if (Test-Path -LiteralPath $newZip) { Remove-Item -LiteralPath $newZip -Force }
Write-Zip (Join-Path $lab 'target') $newZip
$broken = Join-Path $lab 'build-temp/broken'
New-Item -ItemType Directory -Force -Path $broken | Out-Null
Copy-Item -LiteralPath (Join-Path $lab 'target/app') -Destination $broken -Recurse -Force
Copy-Item -LiteralPath (Join-Path $lab 'target/DesktopTimetable.exe') -Destination $broken -Force
$brokenSource = Join-Path $lab 'build-temp/Broken.cs'
[IO.File]::WriteAllText($brokenSource, 'class Broken { static int Main() { return 3; } }')
& $compiler /nologo /target:winexe /platform:x64 ('/out:' + (Join-Path $broken 'app/DesktopTimetable.exe')) $brokenSource
if ($LASTEXITCODE -ne 0) { throw 'Broken-startup fixture build failed' }
Write-Manifest $broken '1.1.1'
$brokenZip = Join-Path $lab 'packages/broken.zip'
if (Test-Path -LiteralPath $brokenZip) { Remove-Item -LiteralPath $brokenZip -Force }
Write-Zip $broken $brokenZip
$unsafeZip = Join-Path $lab 'packages/unsafe.zip'
if (Test-Path -LiteralPath $unsafeZip) { Remove-Item -LiteralPath $unsafeZip -Force }
$archive = [IO.Compression.ZipFile]::Open($unsafeZip, [IO.Compression.ZipArchiveMode]::Create)
try { $entry = $archive.CreateEntry('../timetable.json'); $stream = $entry.Open(); try { $stream.WriteByte(123) } finally { $stream.Dispose() } } finally { $archive.Dispose() }
& $compiler /nologo /target:exe /platform:x64 /reference:System.Web.Extensions.dll ('/out:' + (Join-Path $lab 'server/MockGitHub.exe')) (Join-Path $lab 'MockGitHub.cs')
if ($LASTEXITCODE -ne 0) { throw 'Mock service build failed' }
Write-Host 'Lab built: update-lab/v1.1.0/DesktopTimetable.exe'

function New-UpdatePackage([string]$PortableDirectory, [string]$PackageDirectory, [string]$Version) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression
    $root = [IO.Path]::GetFullPath($PortableDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
    $updateRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) 'updatepack'))
    if (-not $packageRoot.StartsWith($updateRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Package output must stay inside the project updatepack directory.'
    }
    foreach ($path in @($root, $packageRoot)) {
        $current = [IO.DirectoryInfo]::new($path)
        while ($null -ne $current) {
            if ($current.Exists -and ($current.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Do not package through linked directories.' }
            $current = $current.Parent
        }
    }
    New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'app/DesktopTimetable.dll'))
    $actual = '{0}.{1}.{2}' -f $info.FileMajorPart,$info.FileMinorPart,$info.FileBuildPart
    if ($actual -ne $Version) { throw 'Package version differs from application version.' }
    $appRoot = Join-Path $root 'app'
    if (Get-ChildItem -LiteralPath $appRoot -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
        throw 'Do not package linked files or directories.'
    }
    $files = @(Get-Item -LiteralPath (Join-Path $root 'DesktopTimetable.exe')) + @(Get-ChildItem -LiteralPath $appRoot -Recurse -File)
    # The manifest belongs only to the update ZIP, never to the portable folder.
    $manifest = New-Object IO.MemoryStream
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($manifest, $settings)
    try {
        $writer.WriteStartElement('UpdateManifest')
        $writer.WriteElementString('Format','1'); $writer.WriteElementString('Version',$Version)
        $writer.WriteStartElement('Files')
        foreach ($file in $files) {
            if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Do not package linked files.' }
            $relative = $file.FullName.Substring($root.Length + 1).Replace('\','/')
            $writer.WriteStartElement('UpdateFile')
            $writer.WriteElementString('Path',$relative)
            $writer.WriteElementString('Sha256',(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())
            $writer.WriteElementString('Size',$file.Length.ToString([Globalization.CultureInfo]::InvariantCulture))
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement(); $writer.WriteEndElement()
    } finally { $writer.Dispose() }
    $archivePath = Join-Path $packageRoot 'DesktopTimetable-update-win-x64.zip'
    $temporaryArchive = Join-Path $packageRoot ('.package-' + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        $archive = [IO.Compression.ZipFile]::Open($temporaryArchive, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($root.Length + 1).Replace('\','/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
            $entry = $archive.CreateEntry('update-manifest.xml')
            $stream = $entry.Open()
            try { $bytes = $manifest.ToArray(); $stream.Write($bytes, 0, $bytes.Length) }
            finally { $stream.Dispose() }
        } finally { $archive.Dispose() }
        $digest = (Get-FileHash -LiteralPath $temporaryArchive -Algorithm SHA256).Hash.ToLowerInvariant()
        Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
        [IO.File]::WriteAllText($archivePath + '.sha256', $digest + '  ' + [IO.Path]::GetFileName($archivePath) + "`n", [Text.Encoding]::ASCII)
    } finally {
        $manifest.Dispose()
        if (Test-Path -LiteralPath $temporaryArchive) { Remove-Item -LiteralPath $temporaryArchive -Force }
    }
    Write-Host "GitHub Release assets: $archivePath and .sha256"
}

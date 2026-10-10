# Recreates the calendar drawn by legacy/python/app.py::setup_tray.
# Run manually when changing the icon; portable builds embed the resulting ICO.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64)) {
    $large = New-Object Drawing.Bitmap ($size * 4), ($size * 4)
    $graphics = [Drawing.Graphics]::FromImage($large)
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), 2
    $brush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(20, 23, 28))
    $bitmap = $null; $scaled = $null; $stream = $null
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.SmoothingMode = 'AntiAlias'
        $graphics.ScaleTransform($size / 8.0, $size / 8.0)
        $path.AddArc(4, 5, 8, 8, 180, 90)
        $path.AddArc(20, 5, 8, 8, 270, 90)
        $path.AddArc(20, 21, 8, 8, 0, 90)
        $path.AddArc(4, 21, 8, 8, 90, 90)
        $path.CloseFigure()
        $graphics.FillPath($brush, $path)
        $graphics.DrawPath($pen, $path)
        $graphics.DrawLine($pen, 4, 12, 28, 12)
        $graphics.DrawLine($pen, 10, 2, 10, 8)
        $graphics.DrawLine($pen, 22, 2, 22, 8)
        $graphics.DrawLine($pen, 10, 19, 22, 19)
        $graphics.DrawLine($pen, 10, 24, 18, 24)
        $bitmap = New-Object Drawing.Bitmap $size, $size
        $scaled = [Drawing.Graphics]::FromImage($bitmap)
        $scaled.InterpolationMode = 'HighQualityBicubic'
        $scaled.DrawImage($large, 0, 0, $size, $size)
        $stream = New-Object IO.MemoryStream
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $frames += @{ Size = $size; Bytes = $stream.ToArray() }
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
        if ($null -ne $scaled) { $scaled.Dispose() }
        if ($null -ne $bitmap) { $bitmap.Dispose() }
        $brush.Dispose(); $pen.Dispose(); $path.Dispose(); $graphics.Dispose(); $large.Dispose()
    }
}
$output = [IO.File]::Create((Join-Path $PSScriptRoot 'calendar.ico'))
$writer = New-Object IO.BinaryWriter $output
try {
    $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $writer.Write([Byte]$frame.Size); $writer.Write([Byte]$frame.Size)
        $writer.Write([Byte]0); $writer.Write([Byte]0)
        $writer.Write([UInt16]1); $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frame.Bytes.Length); $writer.Write([UInt32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([Byte[]]$frame.Bytes) }
}
finally { $writer.Dispose(); $output.Dispose() }

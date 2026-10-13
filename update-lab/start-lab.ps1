$ErrorActionPreference = 'Stop'
$lab = $PSScriptRoot
$service = Join-Path $lab 'server/MockGitHub.exe'
if (-not (Test-Path -LiteralPath $service)) { throw 'Run build-lab.ps1 first.' }
$running = $false
try { $response = Invoke-WebRequest -Uri 'http://127.0.0.1:18761/' -TimeoutSec 2 -UseBasicParsing; $running = $response.Content.Contains('课表更新模拟服务') } catch { }
if (-not $running) {
    Start-Process -FilePath $service -ArgumentList ('"' + $lab + '" 18761') -WorkingDirectory $lab -WindowStyle Hidden | Out-Null
    Start-Sleep -Milliseconds 500
}
Start-Process 'http://127.0.0.1:18761/'
Start-Process -FilePath (Join-Path $lab 'v1.1.0/DesktopTimetable.exe') -ArgumentList '--update-test-url http://127.0.0.1:18761/ --windowed' -WorkingDirectory (Join-Path $lab 'v1.1.0') -WindowStyle Hidden

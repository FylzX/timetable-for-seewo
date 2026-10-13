$ErrorActionPreference = 'Stop'
$lab = $PSScriptRoot
$projectRoot = Split-Path -Parent $lab
$service = Join-Path $lab 'server/MockGitHub.exe'
$port = 18762
$process = Start-Process -FilePath $service -ArgumentList ('"' + $lab + '" ' + $port) -WorkingDirectory $lab -WindowStyle Hidden -PassThru
try {
    Start-Sleep -Milliseconds 500
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-local'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    & dotnet run --project (Join-Path $lab 'checks/UpdateChecks.csproj') -p:NuGetAudit=false -- $lab ('http://127.0.0.1:' + $port + '/')
    if ($LASTEXITCODE -ne 0) { throw 'Some update scenarios failed. See results/checks.json.' }
} finally { if (-not $process.HasExited) { Stop-Process -Id $process.Id } }

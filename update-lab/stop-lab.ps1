$ErrorActionPreference = 'Stop'
$state = Join-Path $PSScriptRoot 'server-state-18761.json'
if (Test-Path -LiteralPath $state) {
    $info = Get-Content -LiteralPath $state -Raw | ConvertFrom-Json
    $process = Get-Process -Id $info.pid -ErrorAction SilentlyContinue
    if ($null -ne $process -and $process.Path -eq (Join-Path $PSScriptRoot 'server/MockGitHub.exe')) { Stop-Process -Id $process.Id }
}

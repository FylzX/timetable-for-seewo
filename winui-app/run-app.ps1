$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-local'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$packagePath = Join-Path $projectRoot '.dev-packages'
& dotnet run --project (Join-Path $PSScriptRoot 'Timetable.Desktop.csproj') ('-p:RestorePackagesPath=' + $packagePath) -p:NuGetAudit=false -- --data-dir $projectRoot
exit $LASTEXITCODE

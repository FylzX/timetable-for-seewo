param([string]$DataDirectory = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-local'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$project = Join-Path $PSScriptRoot 'Timetable.Editor.csproj'
# Development only: compile and run, without creating a distributable package.
dotnet run --project $project --configuration Debug ('-p:RestorePackagesPath=' + (Join-Path $projectRoot '.dev-packages')) -p:NuGetAudit=false -- --data-dir $DataDirectory
exit $LASTEXITCODE

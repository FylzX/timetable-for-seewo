param([string]$DataDirectory = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Timetable.Editor.csproj'
# Development only: compile and run, without creating a distributable package.
dotnet run --project $project --configuration Debug -- --data-dir $DataDirectory
exit $LASTEXITCODE

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-DocPivotBuildEnvironment -Root $root
$dotnet = Get-DocPivotDotNet -Root $root
$project = Join-Path $root 'tools\DocPivot.UiSnapshot\DocPivot.UiSnapshot.csproj'
$nugetConfig = Join-Path $root 'NuGet.Config'

& $dotnet restore $project `
    --configfile $nugetConfig `
    --locked-mode `
    --ignore-failed-sources `
    --disable-parallel `
    --property:BuildInParallel=false `
    --property:NuGetAudit=false `
    --maxcpucount:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build $project `
    --configuration $Configuration `
    --no-restore `
    --maxcpucount:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$snapshotAssembly = Join-Path `
    $root `
    "tools\DocPivot.UiSnapshot\bin\$Configuration\net10.0-windows\DocPivot.UiSnapshot.dll"
& $dotnet $snapshotAssembly
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

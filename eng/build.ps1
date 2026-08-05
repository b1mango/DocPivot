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
$solution = Join-Path $root 'DocPivot.slnx'
$nugetConfig = Join-Path $root 'NuGet.Config'

& $dotnet restore $solution `
    --configfile $nugetConfig `
    --locked-mode `
    --ignore-failed-sources `
    --disable-parallel `
    --property:BuildInParallel=false `
    --property:NuGetAudit=false `
    --maxcpucount:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build $solution --configuration $Configuration --no-restore --maxcpucount:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

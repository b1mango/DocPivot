[CmdletBinding()]
param(
    [ValidateRange(10, 300)]
    [int]$TimeoutSeconds = 60,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$AppPath,

    [ValidateSet('All', 'ExcelTools', 'ExcelPreservation', 'ExcelSession')]
    [string]$Scope = 'All',

    [string]$ExcelQaWorkbook
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-DocPivotBuildEnvironment -Root $root
$dotnet = Get-DocPivotDotNet -Root $root
$tool = Join-Path $root "tools\DocPivot.OfficeSpike\bin\$Configuration\net10.0-windows\DocPivot.OfficeSpike.dll"

if (-not (Test-Path -LiteralPath $tool)) {
    throw 'Office spike tool is missing. Run eng\build.ps1 first.'
}

$toolArguments = @($TimeoutSeconds)
if (-not [string]::IsNullOrWhiteSpace($AppPath)) {
    $resolvedAppPath = [IO.Path]::GetFullPath($AppPath)
    if (-not (Test-Path -LiteralPath $resolvedAppPath -PathType Leaf)) {
        throw "DocPivot App executable is missing: $resolvedAppPath"
    }

    $toolArguments += $resolvedAppPath
}

$toolArguments += "--scope=$Scope"

if (-not [string]::IsNullOrWhiteSpace($ExcelQaWorkbook)) {
    $resolvedExcelQaWorkbook = [IO.Path]::GetFullPath($ExcelQaWorkbook)
    if (-not (Test-Path -LiteralPath $resolvedExcelQaWorkbook -PathType Leaf)) {
        throw "Excel QA workbook is missing: $resolvedExcelQaWorkbook"
    }

    $toolArguments += "--excel-qa=$resolvedExcelQaWorkbook"
}

& $dotnet $tool @toolArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

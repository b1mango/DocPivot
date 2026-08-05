[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-DocPivotBuildEnvironment -Root $root
$dotnet = Get-DocPivotDotNet -Root $root
$project = Join-Path $root 'src\DocPivot.App\DocPivot.App.csproj'
$nugetConfig = Join-Path $root 'NuGet.Config'
$output = Join-Path $root 'artifacts\preview'

if (Test-Path -LiteralPath $output) {
    $normalizedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $normalizedOutput = [IO.Path]::GetFullPath($output)
    if (-not $normalizedOutput.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean a preview directory outside the repository.'
    }

    Remove-Item -LiteralPath $normalizedOutput -Recurse -Force
}

& $dotnet restore $project `
    --runtime win-x64 `
    --configfile $nugetConfig `
    --disable-parallel `
    --force-evaluate `
    --property:BuildInParallel=false `
    --property:NuGetAudit=false `
    --property:RestorePackagesWithLockFile=true `
    --property:NuGetLockFilePath=obj\preview\packages.lock.json `
    --maxcpucount:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $output `
    --property:PublishSingleFile=true `
    --property:IncludeNativeLibrariesForSelfExtract=true `
    --property:IncludeAllContentForSelfExtract=true `
    --property:EnableCompressionInSingleFile=true `
    --property:DebugType=None `
    --property:DebugSymbols=false `
    --maxcpucount:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$workerRuntimeConfigs = @(
    (Join-Path $output 'DocPivot.OfficeWorker.runtimeconfig.json'),
    (Join-Path $output 'DocPivot.PdfWorker.runtimeconfig.json')
)
foreach ($workerRuntimeConfig in $workerRuntimeConfigs) {
    if (Test-Path -LiteralPath $workerRuntimeConfig) {
        Remove-Item -LiteralPath $workerRuntimeConfig -Force
    }
}

$publishedFiles = @(Get-ChildItem -LiteralPath $output -File)
if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'DocPivot.App.exe') {
    throw 'Preview publishing must produce exactly one DocPivot.App.exe file.'
}

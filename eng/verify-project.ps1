[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$required = @(
    'DocPivot.slnx',
    'Directory.Build.props',
    'Directory.Packages.props',
    'src\DocPivot.App\DocPivot.App.csproj',
    'src\DocPivot.Core\DocPivot.Core.csproj',
    'src\DocPivot.Infrastructure\DocPivot.Infrastructure.csproj',
    'src\DocPivot.OfficeWorker\DocPivot.OfficeWorker.csproj',
    'src\DocPivot.PdfWorker\DocPivot.PdfWorker.csproj',
    'tools\DocPivot.OfficeSpike\DocPivot.OfficeSpike.csproj',
    'tools\DocPivot.UiSnapshot\DocPivot.UiSnapshot.csproj',
    'eng\capture-ui.ps1',
    'tests\DocPivot.App.Tests\DocPivot.App.Tests.csproj',
    'tests\DocPivot.Core.Tests\DocPivot.Core.Tests.csproj',
    'tests\DocPivot.Contract.Tests\DocPivot.Contract.Tests.csproj',
    'tests\DocPivot.Infrastructure.Tests\DocPivot.Infrastructure.Tests.csproj',
    'schemas\worker-message.schema.json',
    'workers\ocr\requirements-dev.lock',
    'eng\dependency-licenses.json',
    'eng\verify-licenses.ps1',
    '.github\workflows\ci.yml'
)

$expectedSolutionProjects = @(
    'src/DocPivot.App/DocPivot.App.csproj',
    'src/DocPivot.Core/DocPivot.Core.csproj',
    'src/DocPivot.Infrastructure/DocPivot.Infrastructure.csproj',
    'src/DocPivot.OfficeWorker/DocPivot.OfficeWorker.csproj',
    'src/DocPivot.PdfWorker/DocPivot.PdfWorker.csproj',
    'tests/DocPivot.App.Tests/DocPivot.App.Tests.csproj',
    'tests/DocPivot.Contract.Tests/DocPivot.Contract.Tests.csproj',
    'tests/DocPivot.Core.Tests/DocPivot.Core.Tests.csproj',
    'tests/DocPivot.Infrastructure.Tests/DocPivot.Infrastructure.Tests.csproj',
    'tools/DocPivot.OfficeSpike/DocPivot.OfficeSpike.csproj',
    'tools/DocPivot.UiSnapshot/DocPivot.UiSnapshot.csproj'
)

foreach ($relativePath in $required) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing required project file: $relativePath"
    }
}

[xml]$solution = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'DocPivot.slnx')
$solutionProjects = @($solution.SelectNodes('//Project') |
    ForEach-Object { $_.GetAttribute('Path') })
$solutionDifference = @(Compare-Object `
    -ReferenceObject ($expectedSolutionProjects | Sort-Object) `
    -DifferenceObject ($solutionProjects | Sort-Object))
if ($solutionProjects.Count -ne $expectedSolutionProjects.Count -or $solutionDifference.Count -ne 0) {
    throw 'DocPivot.slnx project membership does not match the required production, test, and tool projects.'
}

$coreProject = Get-Content -Raw (Join-Path $root 'src\DocPivot.Core\DocPivot.Core.csproj')
if ($coreProject -match 'ProjectReference|UseWPF|Microsoft.Office.Interop') {
    throw 'DocPivot.Core must not reference UI, COM, or infrastructure projects.'
}

$forbiddenComFiles = Get-ChildItem (Join-Path $root 'src') -Recurse -File -Include *.cs,*.csproj |
    Where-Object { $_.FullName -notlike '*DocPivot.OfficeWorker*' } |
    Select-String -Pattern 'Microsoft\.Office\.Interop|GetTypeFromProgID\("(Word|Excel)\.Application"' -List
if ($forbiddenComFiles) {
    throw 'Office COM access is only allowed in DocPivot.OfficeWorker.'
}

$inlinePackageVersions = Get-ChildItem $root -Recurse -File -Filter *.csproj |
    Select-String -Pattern '<PackageReference[^>]+Version=' -List
if ($inlinePackageVersions) {
    throw 'Package versions must be declared centrally in Directory.Packages.props.'
}

Write-Output 'Project alignment checks passed.'

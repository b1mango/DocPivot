[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$designFileName = ([string][char]0x9879) + [char]0x76EE + [char]0x8BBE + [char]0x8BA1 + '.md'

$required = @(
    $designFileName,
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

$design = Get-Content -Raw -Encoding UTF8 (Join-Path $root $designFileName)
if ($design -match '待自动核算|TODO|TBD') {
    throw 'Project design contains unresolved bookkeeping placeholders.'
}

$designLines = Get-Content -Encoding UTF8 (Join-Path $root $designFileName)
$moduleHeadings = @()
for ($index = 0; $index -lt $designLines.Count; $index++) {
    if ($designLines[$index] -match '^### (M[0-9]+) ') {
        $moduleHeadings += [pscustomobject]@{
            Module = $Matches[1]
            Index = $index
        }
    }
}

$expectedModules = @(0..9 | ForEach-Object { "M$_" })
if ($moduleHeadings.Count -ne $expectedModules.Count) {
    throw 'Project design must contain exactly one checklist section for each module M0 through M9.'
}
for ($moduleIndex = 0; $moduleIndex -lt $expectedModules.Count; $moduleIndex++) {
    if ($moduleHeadings[$moduleIndex].Module -ne $expectedModules[$moduleIndex]) {
        throw 'Project design checklist sections must be ordered exactly from M0 through M9.'
    }
}

$totalCompleted = 0
$totalItems = 0
foreach ($headingIndex in 0..($moduleHeadings.Count - 1)) {
    $heading = $moduleHeadings[$headingIndex]
    $sectionEnd = $designLines.Count - 1
    for ($lineIndex = $heading.Index + 1; $lineIndex -lt $designLines.Count; $lineIndex++) {
        if ($designLines[$lineIndex] -match '^#{2,3} ') {
            $sectionEnd = $lineIndex - 1
            break
        }
    }
    $items = @($designLines[($heading.Index + 1)..$sectionEnd] |
        Where-Object { $_ -match '^- \[[x ]\] ' })
    $completed = @($items | Where-Object { $_ -match '^- \[x\] ' }).Count
    $count = $items.Count
    if ($count -eq 0) {
        throw "Project design checklist section $($heading.Module) is empty."
    }
    $progressRows = @($designLines | Where-Object { $_ -match "^\| $($heading.Module) [^|]*\|" })
    $progressMatch = if ($progressRows.Count -eq 1) {
        [regex]::Match(
            $progressRows[0],
            '^\| (M[0-9]+) [^|]*\| ([0-9]+) / ([0-9]+) \| ([0-9.]+)% \|')
    } else {
        $null
    }
    if ($null -eq $progressMatch -or -not $progressMatch.Success) {
        throw "Project design progress row is missing or invalid for $($heading.Module)."
    }

    $rowCompleted = [int]$progressMatch.Groups[2].Value
    $rowCount = [int]$progressMatch.Groups[3].Value
    $rowPercent = [double]::Parse(
        $progressMatch.Groups[4].Value,
        [Globalization.CultureInfo]::InvariantCulture)
    $expectedPercent = [Math]::Round($completed / $count * 100, 1)
    if ($rowCompleted -ne $completed -or
        $rowCount -ne $count -or
        [Math]::Abs($rowPercent - $expectedPercent) -gt 0.01) {
        throw "Project design progress row for $($heading.Module) does not match its checklist."
    }

    $totalCompleted += $completed
    $totalItems += $count
}

$overallLabel = ([string][char]0x603B) + [char]0x8BA1
$overallPattern = '^\| \*\*' + [regex]::Escape($overallLabel) +
    '\*\* \| \*\*([0-9]+) / ([0-9]+)\*\* \| \*\*([0-9.]+)%\*\* \|'
$overallMatches = [regex]::Matches(
    $design,
    $overallPattern,
    [Text.RegularExpressions.RegexOptions]::Multiline)
if ($overallMatches.Count -ne 1) {
    throw "Project design overall progress row is missing or invalid (matches: $($overallMatches.Count))."
}
$overallMatch = $overallMatches[0]

$overallCompleted = [int]$overallMatch.Groups[1].Value
$overallItems = [int]$overallMatch.Groups[2].Value
$overallPercent = [double]::Parse(
    $overallMatch.Groups[3].Value,
    [Globalization.CultureInfo]::InvariantCulture)
$expectedOverallPercent = [Math]::Round($totalCompleted / $totalItems * 100, 1)
if ($overallCompleted -ne $totalCompleted -or
    $overallItems -ne $totalItems -or
    [Math]::Abs($overallPercent - $expectedOverallPercent) -gt 0.01) {
    throw 'Project design overall progress does not match the module checklists.'
}

Write-Output 'Project alignment checks passed.'

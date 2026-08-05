[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$inventory = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'dependency-licenses.json') |
    ConvertFrom-Json
$notices = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'THIRD-PARTY-NOTICES.md')
[xml]$packages = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'Directory.Packages.props')
$pythonInput = Get-Content -Encoding UTF8 (Join-Path $root 'workers\ocr\requirements-dev.in')
$allowedLicenses = @('MIT', 'Apache-2.0', 'BSD-2-Clause', 'PSF-2.0')

$registered = @{}
foreach ($component in $inventory.components) {
    if ($registered.ContainsKey($component.id)) {
        throw "Duplicate dependency license entry: $($component.id)"
    }

    if ($component.license -notin $allowedLicenses) {
        throw "Dependency uses an unapproved license: $($component.id) ($($component.license))"
    }

    if ($notices.IndexOf($component.id, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Dependency is missing from THIRD-PARTY-NOTICES.md: $($component.id)"
    }

    if ($null -ne $component.files) {
        foreach ($file in @($component.files)) {
            $path = Join-Path $root $file.path
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Registered dependency file is missing: $($file.path)"
            }

            $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            if (-not [string]::Equals($actualHash, $file.sha256, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Registered dependency file hash does not match: $($file.path)"
            }
        }
    }

    $registered[$component.id] = $component.version
}

foreach ($package in $packages.Project.ItemGroup.PackageVersion) {
    if (-not $registered.ContainsKey($package.Include)) {
        throw "NuGet dependency is missing from license inventory: $($package.Include)"
    }

    if ($registered[$package.Include] -ne $package.Version) {
        throw "NuGet dependency version does not match license inventory: $($package.Include)"
    }
}

foreach ($requirement in $pythonInput) {
    if ($requirement -notmatch '^([A-Za-z0-9_.-]+)') {
        continue
    }

    $name = $Matches[1]
    if (-not $registered.ContainsKey($name)) {
        throw "Python dependency is missing from license inventory: $name"
    }
}

if ($notices -match 'To verify at lock') {
    throw 'THIRD-PARTY-NOTICES.md contains unverified dependency licenses.'
}

Write-Output 'Dependency license checks passed.'

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $root 'vendor'
$manifest = Get-Content -Raw -Encoding UTF8 (Join-Path $vendor 'manifest.json') | ConvertFrom-Json
$qpdfPrefix = 'qpdf/12.3.2/bin/'
$ghostscriptPrefix = 'ghostscript/10.07.1/runtime/bin/'
$files = @($manifest.files | Where-Object {
    $_.path.StartsWith($qpdfPrefix) -or $_.path.StartsWith($ghostscriptPrefix)
})

function Test-RuntimeFile {
    param($File, [string]$BaseDirectory)

    $path = Join-Path $BaseDirectory $File.path
    return (Test-Path -LiteralPath $path -PathType Leaf) -and
        (Get-Item -LiteralPath $path).Length -eq $File.sizeBytes -and
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $File.sha256
}

# Compare both directions so a newly pinned file cannot silently escape restoration.
$probes = @(
    @{
        Prefix = $qpdfPrefix
        Path = 'src/DocPivot.Infrastructure/Pdf/QpdfToolProbe.cs'
        Pattern = '\["(?<name>[^"/\\]+)"\]\s*=\s*"(?<hash>[a-f0-9]{64})"'
    },
    @{
        Prefix = $ghostscriptPrefix
        Path = 'src/DocPivot.Infrastructure/Pdf/Ghostscript/GhostscriptRuntimeProbe.cs'
        Pattern = '\[Path\.Combine\("bin",\s*"(?<name>[^"/\\]+)"\)\]\s*=\s*"(?<hash>[a-f0-9]{64})"'
    }
)
foreach ($probe in $probes) {
    $source = Get-Content -Raw (Join-Path $root $probe.Path)
    $pins = @([regex]::Matches($source, $probe.Pattern) | ForEach-Object {
        "$($probe.Prefix)$($_.Groups['name'].Value)=$($_.Groups['hash'].Value)"
    })
    $entries = @($files | Where-Object { $_.path.StartsWith($probe.Prefix) } |
        ForEach-Object { "$($_.path)=$($_.sha256)" })
    if ($pins.Count -eq 0 -or $entries.Count -eq 0) {
        throw "No runtime hashes found for $($probe.Path)."
    }
    $difference = @(Compare-Object $pins $entries)
    if ($difference.Count -ne 0) {
        throw "Probe/manifest hash differences for $($probe.Path): $($difference | Out-String)"
    }
}

$missing = @($files | Where-Object { -not (Test-RuntimeFile $_ $vendor) })
if ($missing.Count -eq 0) {
    Write-Output 'Pinned PDF engines already match the manifest and probes.'
    return
}

$temp = Join-Path ([IO.Path]::GetTempPath()) "DocPivot-vendor-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $zipPath = Join-Path $temp 'qpdf-12.3.2-msvc64.zip'
    Invoke-WebRequest -Uri 'https://github.com/qpdf/qpdf/releases/download/v12.3.2/qpdf-12.3.2-msvc64.zip' -OutFile $zipPath
    if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ne
        '8941870a604e7c87ed24566b038d46c24ce76616254d2383c578f60c0677f202') {
        throw 'qpdf ZIP SHA256 does not match vendor/README.md.'
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name.StartsWith('/') -or $name.Contains(':') -or $name.Split('/') -contains '..') {
                throw "Unsafe qpdf ZIP entry: $name"
            }
        }
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name -notmatch '^qpdf-12\.3\.2-msvc64/bin/(.+)$' -or $name.EndsWith('/')) {
                continue
            }
            $destination = Join-Path $temp ($qpdfPrefix + $Matches[1])
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
        }
    }
    finally {
        $archive.Dispose()
    }

    $installer = Join-Path $vendor 'ghostscript/10.07.1/download/gs10071w64.exe'
    $sha512 = 'bae62c525ffe6d6d8a747dc92256a16c90fa20c59be5ba98494cd2e408395528542212b94e461acd68f09dc0fd5b96ab04072bb087554854335a9b2ad28a5eb9'
    $checksumLines = @(Get-Content (Join-Path $vendor 'ghostscript/10.07.1/download/SHA512SUMS') |
        Where-Object { $_ -match '^([a-f0-9]{128})\s+gs10071w64\.exe$' })
    if ($checksumLines.Count -ne 1 -or ($checksumLines[0] -split '\s+')[0] -ne $sha512 -or
        (Get-FileHash -LiteralPath $installer -Algorithm SHA512).Hash -ne $sha512 -or
        (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne
        '3a4c28d0aac47aa7cccd35a5932c55110376e9dbd966898dde388b7faba444a4') {
        throw 'Ghostscript installer does not match vendor/README.md and SHA512SUMS.'
    }

    $sevenZip = Get-Command 7z -ErrorAction SilentlyContinue
    if ($null -eq $sevenZip) {
        throw '7-Zip (7z) was not found in PATH. It is required to unpack Ghostscript without running its installer.'
    }
    $unpacked = Join-Path $temp 'ghostscript-unpacked'
    & $sevenZip.Source x $installer "-o$unpacked" '-y' 'bin/gswin64c.exe' 'bin/gsdll64.dll'
    if ($LASTEXITCODE -ne 0) { throw "7-Zip extraction failed with exit code $LASTEXITCODE." }

    $ghostscriptBin = Join-Path $temp $ghostscriptPrefix
    New-Item -ItemType Directory -Force -Path $ghostscriptBin | Out-Null
    foreach ($name in @('gswin64c.exe', 'gsdll64.dll')) {
        Copy-Item -LiteralPath (Join-Path $unpacked "bin/$name") -Destination $ghostscriptBin
    }
    foreach ($name in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
        Copy-Item -LiteralPath (Join-Path $temp "$qpdfPrefix$name") -Destination $ghostscriptBin
    }

    # Validate the staged payload before replacing any runtime files.
    foreach ($file in $files) {
        if (-not (Test-RuntimeFile $file $temp)) {
            throw "Restored file length or SHA256 mismatch: $($file.path)"
        }
    }
    foreach ($file in $files) {
        $destination = Join-Path $vendor $file.path
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath (Join-Path $temp $file.path) -Destination $destination -Force
    }
    foreach ($file in $files) {
        if (-not (Test-RuntimeFile $file $vendor)) {
            throw "Installed file length or SHA256 mismatch: $($file.path)"
        }
    }
    Write-Output "Restored and verified $($files.Count) PDF runtime files."
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}

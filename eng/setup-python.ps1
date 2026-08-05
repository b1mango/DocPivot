[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$virtualEnvironment = Join-Path $root '.venv'
$python = Join-Path $virtualEnvironment 'Scripts\python.exe'
$lockFile = Join-Path $root 'workers\ocr\requirements-dev.lock'

if (-not (Test-Path -LiteralPath $python)) {
    & python -m venv $virtualEnvironment
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& $python -m pip install `
    --disable-pip-version-check `
    --require-hashes `
    --requirement $lockFile
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

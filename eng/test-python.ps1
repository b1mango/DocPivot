[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$worker = Join-Path $root 'workers\ocr'
$python = Join-Path $root '.venv\Scripts\python.exe'

if (-not (Test-Path -LiteralPath $python)) {
    throw 'Python environment is missing. Run eng\setup-python.ps1 first.'
}

Push-Location $worker
try {
    & $python -m ruff check src tests
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $python -m mypy src tests
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $python -m pytest
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}

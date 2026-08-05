function Initialize-DocPivotBuildEnvironment {
    param([Parameter(Mandatory)][string]$Root)

    $env:DOTNET_CLI_HOME = Join-Path $Root '.dotnet-home'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:APPDATA = Join-Path $Root '.appdata'
    $env:LOCALAPPDATA = Join-Path $Root '.appdata\local'
    $env:NUGET_PACKAGES = Join-Path $Root '.nuget\packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $Root '.nuget\http-cache'
}

function Get-DocPivotDotNet {
    param([Parameter(Mandatory)][string]$Root)

    $localDotnet = Join-Path $Root '.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localDotnet) {
        return $localDotnet
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw '.NET SDK was not found in .dotnet or PATH.'
    }

    return $command.Source
}

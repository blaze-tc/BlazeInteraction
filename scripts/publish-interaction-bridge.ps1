[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts\publish\BlazeInteractionBridge\$Runtime"
}

$publishRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$pathRoot = [System.IO.Path]::GetPathRoot($publishRoot)
if ([string]::Equals($publishRoot.TrimEnd('\'), $pathRoot.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The publish output cannot be a filesystem root.'
}

if (Test-Path -LiteralPath $publishRoot) {
    if (@(Get-ChildItem -LiteralPath $publishRoot -Force).Count -ne 0) {
        throw "The publish output must be empty: $publishRoot"
    }
}
else {
    [System.IO.Directory]::CreateDirectory($publishRoot) | Out-Null
}

$bridgeProject = Join-Path $repositoryRoot 'src\Blaze.Interaction.Bridge.Wpf\Blaze.Interaction.Bridge.Wpf.csproj'
$radarProject = Join-Path $repositoryRoot 'providers\Radar\Blaze.Provider.Radar\Blaze.Provider.Radar.csproj'
$radarDirectory = Join-Path $publishRoot 'Providers\Radar'
[System.IO.Directory]::CreateDirectory($radarDirectory) | Out-Null

Push-Location $repositoryRoot
try {
    & dotnet publish $bridgeProject -c Release -r $Runtime --self-contained false `
        -p:PublishSingleFile=false -p:UseAppHost=true --nologo -o $publishRoot
    if ($LASTEXITCODE -ne 0) {
        throw "BlazeInteractionBridge publish failed with exit code $LASTEXITCODE."
    }

    & dotnet publish $radarProject -c Release -r $Runtime --self-contained false `
        -p:PublishSingleFile=false -p:UseAppHost=false --nologo -o $radarDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Radar Provider publish failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$executables = @(Get-ChildItem -LiteralPath $publishRoot -Filter '*.exe' -File -Recurse)
if ($executables.Count -ne 1 -or $executables[0].Name -cne 'BlazeInteractionBridge.exe') {
    throw 'Interaction publish must contain exactly one executable named BlazeInteractionBridge.exe.'
}

$manifestPath = Join-Path $radarDirectory 'provider.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'The external Radar Provider manifest is missing.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.id -cne 'blaze.radar.f10f20' -or
    $manifest.providerApiVersion -ne 1 -or
    $manifest.entryAssembly -cne 'Blaze.Provider.Radar.dll' -or
    $manifest.entryType -cne 'Blaze.Provider.Radar.RadarPlugin') {
    throw 'The external Radar Provider manifest does not match Provider API 1.'
}

$entryAssembly = Join-Path $radarDirectory $manifest.entryAssembly
if (-not (Test-Path -LiteralPath $entryAssembly -PathType Leaf)) {
    throw 'The external Radar Provider entry assembly is missing.'
}

$stream = [System.IO.File]::OpenRead($executables[0].FullName)
try {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = [System.BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}
finally {
    $stream.Dispose()
}
Write-Host "BlazeInteractionBridge published to: $publishRoot"
Write-Host "BlazeInteractionBridge.exe SHA-256: $hash"
Write-Host "External Radar Provider: $radarDirectory"

[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory,
    [switch]$EmbedUnityPackage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'bridge-release-functions.ps1')

$expectedVersion = '1.1.1'

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
$cameraVisionProject = Join-Path $repositoryRoot 'providers\CameraVision\Blaze.Provider.CameraVision\Blaze.Provider.CameraVision.csproj'
$radarDirectory = Join-Path $publishRoot 'Providers\Radar'
$cameraVisionDirectory = Join-Path $publishRoot 'Providers\CameraVision'
[System.IO.Directory]::CreateDirectory($radarDirectory) | Out-Null
[System.IO.Directory]::CreateDirectory($cameraVisionDirectory) | Out-Null

Push-Location $repositoryRoot
try {
    & dotnet publish $bridgeProject -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:UseAppHost=true --nologo -o $publishRoot
    if ($LASTEXITCODE -ne 0) {
        throw "BlazeInteractionBridge publish failed with exit code $LASTEXITCODE."
    }

    & dotnet publish $radarProject -c Release -r $Runtime --self-contained false `
        -p:PublishSingleFile=false -p:UseAppHost=false --nologo -o $radarDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Radar Provider publish failed with exit code $LASTEXITCODE."
    }

    & dotnet publish $cameraVisionProject -c Release -r $Runtime --self-contained false `
        -p:PublishSingleFile=false -p:UseAppHost=false --nologo -o $cameraVisionDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "CameraVision Provider publish failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$optionalCrashDumper = Join-Path $publishRoot 'createdump.exe'
if (Test-Path -LiteralPath $optionalCrashDumper -PathType Leaf) {
    Remove-Item -LiteralPath $optionalCrashDumper -Force
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

$cameraVisionManifestPath = Join-Path $cameraVisionDirectory 'provider.json'
if (-not (Test-Path -LiteralPath $cameraVisionManifestPath -PathType Leaf)) {
    throw 'The external CameraVision Provider manifest is missing.'
}

$cameraVisionManifest = Get-Content -LiteralPath $cameraVisionManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($cameraVisionManifest.id -cne 'blaze.camera.vision' -or
    $cameraVisionManifest.providerApiVersion -ne 1 -or
    $cameraVisionManifest.entryAssembly -cne 'Blaze.Provider.CameraVision.dll' -or
    $cameraVisionManifest.entryType -cne 'Blaze.Provider.CameraVision.CameraVisionPlugin') {
    throw 'The external CameraVision Provider manifest does not match Provider API 1.'
}

$cameraVisionEntryAssembly = Join-Path $cameraVisionDirectory $cameraVisionManifest.entryAssembly
if (-not (Test-Path -LiteralPath $cameraVisionEntryAssembly -PathType Leaf)) {
    throw 'The external CameraVision Provider entry assembly is missing.'
}
$cameraVisionNativeLibrary = @(Get-ChildItem -LiteralPath $cameraVisionDirectory `
    -Filter 'OpenCvSharpExtern.dll' -File -Recurse)
if ($cameraVisionNativeLibrary.Count -ne 1) {
    throw 'CameraVision publish must contain exactly one OpenCvSharpExtern.dll native runtime.'
}

$handProvenancePath = Join-Path $repositoryRoot 'eng\mediapipe-hand.json'
try {
    $handProvenance = Get-Content -LiteralPath $handProvenancePath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "MediaPipe hand provenance manifest is invalid JSON: $($_.Exception.Message)"
}
$handNativeRelativePath = 'runtimes/win-x64/native/Blaze.HandTracking.Native.dll'
$handModelRelativePath = 'models/hand_landmarker.task'
$handNativePath = Join-Path $cameraVisionDirectory $handNativeRelativePath
$handModelPath = Join-Path $cameraVisionDirectory $handModelRelativePath
foreach ($asset in @(
    [PSCustomObject]@{ Label = 'native'; Path = $handNativePath; ExpectedHash = [string]$handProvenance.nativeDllSha256 },
    [PSCustomObject]@{ Label = 'model'; Path = $handModelPath; ExpectedHash = [string]$handProvenance.modelSha256 })) {
    if (-not (Test-Path -LiteralPath $asset.Path -PathType Leaf)) {
        throw "CameraVision hand $($asset.Label) asset is missing: $($asset.Path)"
    }
    $actualHash = Get-Sha256Hash -Path $asset.Path
    if ($actualHash -cne $asset.ExpectedHash.ToLowerInvariant()) {
        throw "CameraVision hand $($asset.Label) SHA-256 mismatch. Expected $($asset.ExpectedHash), actual $actualHash."
    }
}
$handRuntimeManifest = [ordered]@{
    schemaVersion = 1
    abiVersion = [int]$handProvenance.abiVersion
    nativeLibrary = [ordered]@{
        path = $handNativeRelativePath
        sha256 = ([string]$handProvenance.nativeDllSha256).ToLowerInvariant()
    }
    model = [ordered]@{
        path = $handModelRelativePath
        sha256 = ([string]$handProvenance.modelSha256).ToLowerInvariant()
    }
}
[System.IO.File]::WriteAllText(
    (Join-Path $cameraVisionDirectory 'hand-runtime.json'),
    ($handRuntimeManifest | ConvertTo-Json -Depth 4),
    [System.Text.UTF8Encoding]::new($false))
Assert-CameraVisionHandRuntime -Directory $cameraVisionDirectory

[System.IO.File]::WriteAllText(
    (Join-Path $publishRoot 'bridge-version.txt'),
    $expectedVersion,
    [System.Text.UTF8Encoding]::new($false))
Assert-InteractionBridgePayload -Directory $publishRoot -ExpectedVersion $expectedVersion

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
Write-Host "External CameraVision Provider: $cameraVisionDirectory"

if ($EmbedUnityPackage) {
    $embeddedDirectory = [System.IO.Path]::GetFullPath((
        Join-Path $repositoryRoot "UnityPackage\com.blaze.interaction\Bridge~\$Runtime"))
    $embeddedHash = Copy-ValidatedInteractionBridgePayload `
        -SourceDirectory $publishRoot `
        -DestinationDirectory $embeddedDirectory `
        -ExpectedDestinationDirectory $embeddedDirectory `
        -ExpectedVersion $expectedVersion
    Write-Host "Embedded BlazeInteractionBridge.exe SHA-256: $embeddedHash"
}

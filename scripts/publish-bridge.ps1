[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [switch]$SkipUnityPackageEmbedding
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'bridge-release-functions.ps1')

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$expectedOutputDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts\publish\RadarBridge\win-x64'))
$outputDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "artifacts\publish\RadarBridge\$Runtime"))
$project = Join-Path $repositoryRoot 'src\Radar.Bridge.Wpf\Radar.Bridge.Wpf.csproj'
$packageRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'UnityPackage\com.blaze.radar'))
$expectedEmbeddedDirectory = [System.IO.Path]::GetFullPath((Join-Path $packageRoot 'Bridge~\win-x64'))
$embeddedDirectory = [System.IO.Path]::GetFullPath((Join-Path $packageRoot "Bridge~\$Runtime"))
$packageJsonPath = Join-Path $packageRoot 'package.json'
$packageVersion = (Get-Content -LiteralPath $packageJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json).version
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$defaultProfile = Join-Path $repositoryRoot 'config\default-profile.json'
$f20Profile = Join-Path $repositoryRoot 'config\f20-profile.json'

# Every destructive action happens only after this shared, executable preflight.
Invoke-BridgePublishPreflight -OutputDirectory $outputDirectory -ExpectedOutputDirectory $expectedOutputDirectory `
    -EmbeddedDirectory $embeddedDirectory -ExpectedEmbeddedDirectory $expectedEmbeddedDirectory `
    -FrameworkDependent $FrameworkDependent.IsPresent -SkipUnityPackageEmbedding $SkipUnityPackageEmbedding.IsPresent

Push-Location $repositoryRoot
try {
    Remove-ValidatedDirectory -Path $outputDirectory -ExpectedPath $expectedOutputDirectory -Label 'Publish output'

    dotnet publish $project -c Release -r $Runtime --self-contained $selfContained `
        -p:PublishSingleFile=false -o $outputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

    if ($FrameworkDependent) {
        Copy-Schema2Profiles -DefaultProfile $defaultProfile -F20Profile $f20Profile `
            -DestinationDirectory (Join-Path $outputDirectory 'profiles')
        Write-BridgeVersionMarker -Directory $outputDirectory -Version $packageVersion
    }
    else {
        Prepare-BridgePublishOutput -Directory $outputDirectory -DefaultProfile $defaultProfile `
            -F20Profile $f20Profile -Version $packageVersion
    }

    $executable = Join-Path $outputDirectory 'RadarBridge.exe'
    $publishedHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    $publishedFileCount = @(Get-ChildItem -LiteralPath $outputDirectory -File -Recurse).Count

    if (-not $SkipUnityPackageEmbedding) {
        Copy-ValidatedBridgePayload -SourceDirectory $outputDirectory -DestinationDirectory $embeddedDirectory `
            -ExpectedDestinationDirectory $expectedEmbeddedDirectory -ExpectedVersion $packageVersion

        $embeddedExecutable = Join-Path $embeddedDirectory 'RadarBridge.exe'
        $embeddedHash = (Get-FileHash -LiteralPath $embeddedExecutable -Algorithm SHA256).Hash
        if (-not [string]::Equals($publishedHash, $embeddedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Published and embedded RadarBridge.exe hashes differ.'
        }

        $embeddedFileCount = @(Get-ChildItem -LiteralPath $embeddedDirectory -File -Recurse).Count
        if ($publishedFileCount -ne $embeddedFileCount) {
            throw "Published and embedded file counts differ: $publishedFileCount versus $embeddedFileCount."
        }

        Write-Host "RadarBridge embedded in Unity package: $embeddedDirectory"
        Write-Host "Embedded RadarBridge.exe SHA-256: $embeddedHash"
        Write-Host "Embedded File count: $embeddedFileCount"
    }

    Write-Host "RadarBridge published to: $outputDirectory"
    Write-Host "Published RadarBridge.exe SHA-256: $publishedHash"
    Write-Host "Published File count: $publishedFileCount"
}
finally {
    Pop-Location
}

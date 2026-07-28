[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [switch]$SkipUnityPackageEmbedding
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-ExactDeletionTarget {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$ExpectedPath,
        [Parameter(Mandatory)] [string]$Label
    )

    if (-not [System.IO.Path]::IsPathRooted($Path)) {
        throw "$Label deletion target is not absolute: $Path"
    }

    $resolvedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $resolvedExpected = [System.IO.Path]::GetFullPath($ExpectedPath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    if (-not [string]::Equals($resolvedPath, $resolvedExpected, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label deletion target was not the exact approved path. Expected '$resolvedExpected', got '$resolvedPath'."
    }
}

function Copy-Schema2Profile {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$DestinationDirectory
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required Bridge profile was not found: $Source"
    }

    $profile = Get-Content -LiteralPath $Source -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($profile.schemaVersion -ne 2) {
        throw "Bridge profile '$Source' must use schemaVersion 2; found '$($profile.schemaVersion)'."
    }

    Copy-Item -LiteralPath $Source -Destination $DestinationDirectory -Force
}

function Assert-SelfContainedOutput {
    param([Parameter(Mandatory)] [string]$Directory)

    $runtimeConfigPath = Join-Path $Directory 'RadarBridge.runtimeconfig.json'
    $coreRuntimePath = Join-Path $Directory 'coreclr.dll'
    if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $coreRuntimePath -PathType Leaf)) {
        throw "RadarBridge output is not self-contained: runtimeconfig or coreclr.dll is missing from '$Directory'."
    }

    $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $optionNames = @($runtimeConfig.runtimeOptions.PSObject.Properties.Name)
    if ($optionNames -contains 'framework' -or $optionNames -contains 'frameworks' -or
        $optionNames -notcontains 'includedFrameworks' -or $runtimeConfig.runtimeOptions.includedFrameworks.Count -lt 1) {
        throw "RadarBridge runtimeconfig describes framework-dependent output; embedding is forbidden."
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$expectedOutputDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts\publish\RadarBridge\win-x64'))
$outputDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "artifacts\publish\RadarBridge\$Runtime"))
$project = Join-Path $repositoryRoot 'src\Radar.Bridge.Wpf\Radar.Bridge.Wpf.csproj'
$packageRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'UnityPackage\com.blaze.radar'))
$expectedEmbeddedDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $packageRoot 'Bridge~\win-x64'))
$embeddedDirectory = [System.IO.Path]::GetFullPath((Join-Path $packageRoot "Bridge~\$Runtime"))
$packageJsonPath = Join-Path $packageRoot 'package.json'
$packageVersion = (Get-Content -LiteralPath $packageJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json).version
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

Assert-ExactDeletionTarget -Path $outputDirectory -ExpectedPath $expectedOutputDirectory -Label 'Publish output'
Assert-ExactDeletionTarget -Path $embeddedDirectory -ExpectedPath $expectedEmbeddedDirectory -Label 'Embedded Bridge'

if ($FrameworkDependent -and -not $SkipUnityPackageEmbedding) {
    throw 'A framework-dependent Bridge cannot be embedded in the Unity package. Use -SkipUnityPackageEmbedding or publish self-contained.'
}

Push-Location $repositoryRoot
try {
    if (Test-Path -LiteralPath $outputDirectory) {
        Remove-Item -LiteralPath $outputDirectory -Recurse -Force
    }

    dotnet publish $project -c Release -r $Runtime --self-contained $selfContained `
        -p:PublishSingleFile=false -o $outputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

    $profilesDirectory = Join-Path $outputDirectory 'profiles'
    New-Item -ItemType Directory -Force -Path $profilesDirectory | Out-Null
    Copy-Schema2Profile -Source (Join-Path $repositoryRoot 'config\default-profile.json') `
        -DestinationDirectory $profilesDirectory
    Copy-Schema2Profile -Source (Join-Path $repositoryRoot 'config\f20-profile.json') `
        -DestinationDirectory $profilesDirectory

    $versionMarker = Join-Path $outputDirectory 'bridge-version.txt'
    [System.IO.File]::WriteAllText(
        $versionMarker,
        $packageVersion,
        [System.Text.UTF8Encoding]::new($false))

    $executable = Join-Path $outputDirectory 'RadarBridge.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Publish completed without RadarBridge.exe: $executable"
    }

    if (-not $FrameworkDependent) {
        Assert-SelfContainedOutput -Directory $outputDirectory
    }

    $publishedHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    $publishedFileCount = @(Get-ChildItem -LiteralPath $outputDirectory -File -Recurse).Count

    if (-not $SkipUnityPackageEmbedding) {
        if (Test-Path -LiteralPath $embeddedDirectory) {
            Remove-Item -LiteralPath $embeddedDirectory -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path $embeddedDirectory | Out-Null
        Get-ChildItem -LiteralPath $outputDirectory | Copy-Item -Destination $embeddedDirectory -Recurse -Force
        Assert-SelfContainedOutput -Directory $embeddedDirectory

        foreach ($profileName in @('default-profile.json', 'f20-profile.json')) {
            $embeddedProfile = Join-Path $embeddedDirectory "profiles\$profileName"
            $schemaVersion = (Get-Content -LiteralPath $embeddedProfile -Raw -Encoding UTF8 | ConvertFrom-Json).schemaVersion
            if ($schemaVersion -ne 2) {
                throw "Embedded profile '$profileName' must use schemaVersion 2."
            }
        }

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

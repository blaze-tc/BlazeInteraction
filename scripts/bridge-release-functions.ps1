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

function Assert-EmbeddingMode {
    param([bool]$FrameworkDependent, [bool]$SkipUnityPackageEmbedding)
    if ($FrameworkDependent -and -not $SkipUnityPackageEmbedding) {
        throw 'A framework-dependent Bridge cannot be embedded in the Unity package. Use -SkipUnityPackageEmbedding or publish self-contained.'
    }
}

function Invoke-BridgePublishPreflight {
    param(
        [Parameter(Mandatory)] [string]$OutputDirectory,
        [Parameter(Mandatory)] [string]$ExpectedOutputDirectory,
        [Parameter(Mandatory)] [string]$EmbeddedDirectory,
        [Parameter(Mandatory)] [string]$ExpectedEmbeddedDirectory,
        [bool]$FrameworkDependent,
        [bool]$SkipUnityPackageEmbedding
    )

    Assert-ExactDeletionTarget -Path $OutputDirectory -ExpectedPath $ExpectedOutputDirectory -Label 'Publish output'
    Assert-ExactDeletionTarget -Path $EmbeddedDirectory -ExpectedPath $ExpectedEmbeddedDirectory -Label 'Embedded Bridge'
    Assert-EmbeddingMode -FrameworkDependent $FrameworkDependent -SkipUnityPackageEmbedding $SkipUnityPackageEmbedding
}

function Remove-ValidatedDirectory {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$ExpectedPath,
        [Parameter(Mandatory)] [string]$Label
    )

    Assert-ExactDeletionTarget -Path $Path -ExpectedPath $ExpectedPath -Label $Label
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Assert-Schema2Profile {
    param([Parameter(Mandatory)] [string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required Bridge profile was not found: $Path"
    }
    try { $profile = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "Bridge profile '$Path' is not valid JSON: $($_.Exception.Message)" }
    if ($profile.schemaVersion -ne 2) {
        throw "Bridge profile '$Path' must use schemaVersion 2; found '$($profile.schemaVersion)'."
    }
}

function Assert-SelfContainedRuntime {
    param([Parameter(Mandatory)] [string]$Directory)

    foreach ($name in @(
        'RadarBridge.exe', 'RadarBridge.dll', 'RadarBridge.deps.json', 'RadarBridge.runtimeconfig.json',
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
        'PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll', 'wpfgfx_cor3.dll')) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "RadarBridge self-contained runtime dependency is missing: $path"
        }
    }

    $runtimeConfigPath = Join-Path $Directory 'RadarBridge.runtimeconfig.json'
    try { $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "RadarBridge.runtimeconfig.json is not valid JSON: $($_.Exception.Message)" }
    $optionNames = @($runtimeConfig.runtimeOptions.PSObject.Properties.Name)
    if ($optionNames -contains 'framework' -or $optionNames -contains 'frameworks' -or
        $optionNames -notcontains 'includedFrameworks') {
        throw 'RadarBridge runtimeconfig describes framework-dependent output; embedding is forbidden.'
    }
    $frameworkNames = @($runtimeConfig.runtimeOptions.includedFrameworks | ForEach-Object { $_.name })
    foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        if ($frameworkNames -notcontains $framework) {
            throw "RadarBridge runtimeconfig is missing included framework '$framework'."
        }
    }
}

function Write-BridgeVersionMarker {
    param([Parameter(Mandatory)] [string]$Directory, [Parameter(Mandatory)] [string]$Version)
    [System.IO.File]::WriteAllText(
        (Join-Path $Directory 'bridge-version.txt'),
        $Version,
        [System.Text.UTF8Encoding]::new($false))
}

function Copy-Schema2Profiles {
    param(
        [Parameter(Mandatory)] [string]$DefaultProfile,
        [Parameter(Mandatory)] [string]$F20Profile,
        [Parameter(Mandatory)] [string]$DestinationDirectory
    )
    Assert-Schema2Profile -Path $DefaultProfile
    Assert-Schema2Profile -Path $F20Profile
    New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
    Copy-Item -LiteralPath $DefaultProfile -Destination (Join-Path $DestinationDirectory 'default-profile.json') -Force
    Copy-Item -LiteralPath $F20Profile -Destination (Join-Path $DestinationDirectory 'f20-profile.json') -Force
}

function Assert-BridgePublishOutput {
    param([Parameter(Mandatory)] [string]$Directory, [Parameter(Mandatory)] [string]$ExpectedVersion)
    Assert-SelfContainedRuntime -Directory $Directory
    $marker = Join-Path $Directory 'bridge-version.txt'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw "Bridge version marker is missing: $marker" }
    $actualVersion = (Get-Content -LiteralPath $marker -Raw -Encoding UTF8).Trim()
    if ($actualVersion -ne $ExpectedVersion) { throw "Bridge version marker expected '$ExpectedVersion', found '$actualVersion'." }
    Assert-Schema2Profile -Path (Join-Path $Directory 'profiles\default-profile.json')
    Assert-Schema2Profile -Path (Join-Path $Directory 'profiles\f20-profile.json')
}

function Prepare-BridgePublishOutput {
    param(
        [Parameter(Mandatory)] [string]$Directory,
        [Parameter(Mandatory)] [string]$DefaultProfile,
        [Parameter(Mandatory)] [string]$F20Profile,
        [Parameter(Mandatory)] [string]$Version
    )

    # Validate every non-generated input before mutating the prepared directory.
    Assert-SelfContainedRuntime -Directory $Directory
    Assert-Schema2Profile -Path $DefaultProfile
    Assert-Schema2Profile -Path $F20Profile
    Copy-Schema2Profiles -DefaultProfile $DefaultProfile -F20Profile $F20Profile -DestinationDirectory (Join-Path $Directory 'profiles')
    Write-BridgeVersionMarker -Directory $Directory -Version $Version
    Assert-BridgePublishOutput -Directory $Directory -ExpectedVersion $Version
}

function Copy-ValidatedBridgePayload {
    param(
        [Parameter(Mandatory)] [string]$SourceDirectory,
        [Parameter(Mandatory)] [string]$DestinationDirectory,
        [Parameter(Mandatory)] [string]$ExpectedDestinationDirectory,
        [Parameter(Mandatory)] [string]$ExpectedVersion
    )
    Assert-BridgePublishOutput -Directory $SourceDirectory -ExpectedVersion $ExpectedVersion
    Remove-ValidatedDirectory -Path $DestinationDirectory -ExpectedPath $ExpectedDestinationDirectory -Label 'Embedded Bridge'
    New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
    Get-ChildItem -LiteralPath $SourceDirectory | Copy-Item -Destination $DestinationDirectory -Recurse -Force
    Assert-BridgePublishOutput -Directory $DestinationDirectory -ExpectedVersion $ExpectedVersion
}

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

function Get-JsonPropertyValue {
    param(
        [AllowNull()] [object]$Object,
        [Parameter(Mandatory)] [string]$Name
    )
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Assert-JsonObject {
    param(
        [AllowNull()] [object]$Value,
        [Parameter(Mandatory)] [string]$Description
    )
    if ($null -eq $Value -or $Value -isnot [PSCustomObject]) {
        throw "$Description must be an object."
    }
}

function Get-PublishedAssetRelativePath {
    param(
        [Parameter(Mandatory)] [string]$AssetPath,
        [Parameter(Mandatory)] [string]$AssetType,
        [Parameter(Mandatory)] [PSCustomObject]$Metadata
    )

    $fileName = [System.IO.Path]::GetFileName($AssetPath.Replace('/', '\'))
    if ([string]::IsNullOrWhiteSpace($fileName)) {
        throw "$AssetType asset path is invalid: '$AssetPath'."
    }
    if ($AssetType -ieq 'runtime' -or $AssetType -ieq 'native') { return $fileName }
    if ($AssetType -ine 'resources') {
        throw "runtimeTargets asset '$AssetPath' has unsupported assetType '$AssetType'."
    }

    $locale = [string](Get-JsonPropertyValue -Object $Metadata -Name 'locale')
    if ([string]::IsNullOrWhiteSpace($locale) -or $locale -eq '.' -or $locale -eq '..' -or
        $locale.Contains('/') -or $locale.Contains('\')) {
        throw "resources asset '$AssetPath' has invalid locale '$locale'."
    }
    return [System.IO.Path]::Combine($locale, $fileName)
}

function Assert-DependencyManifest {
    param(
        [Parameter(Mandatory)] [string]$Directory,
        [Parameter(Mandatory)] [string]$ExpectedRuntimeIdentifier
    )

    $depsPath = Join-Path $Directory 'RadarBridge.deps.json'
    try { $deps = Get-Content -LiteralPath $depsPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "RadarBridge.deps.json must contain valid JSON: $($_.Exception.Message)" }
    Assert-JsonObject -Value $deps -Description 'RadarBridge.deps.json root'

    $runtimeTarget = Get-JsonPropertyValue -Object $deps -Name 'runtimeTarget'
    Assert-JsonObject -Value $runtimeTarget -Description 'RadarBridge.deps.json runtimeTarget'
    $targetName = [string](Get-JsonPropertyValue -Object $runtimeTarget -Name 'name')
    if ([string]::IsNullOrWhiteSpace($targetName)) { throw 'RadarBridge.deps.json is missing runtimeTarget.name.' }
    $separatorIndex = $targetName.LastIndexOf('/')
    if ($separatorIndex -lt 0 -or $separatorIndex -eq ($targetName.Length - 1)) {
        throw "RadarBridge.deps.json runtimeTarget.name does not identify RID '$ExpectedRuntimeIdentifier': $targetName"
    }
    $runtimeIdentifier = $targetName.Substring($separatorIndex + 1)
    if ($runtimeIdentifier -ine $ExpectedRuntimeIdentifier) {
        throw "RadarBridge.deps.json runtime target RID must be '$ExpectedRuntimeIdentifier', found '$runtimeIdentifier'."
    }

    $targets = Get-JsonPropertyValue -Object $deps -Name 'targets'
    Assert-JsonObject -Value $targets -Description 'RadarBridge.deps.json targets'
    $selectedTarget = Get-JsonPropertyValue -Object $targets -Name $targetName
    if ($null -eq $selectedTarget) { throw "RadarBridge.deps.json is missing selected target '$targetName'." }
    Assert-JsonObject -Value $selectedTarget -Description "RadarBridge.deps.json selected target '$targetName'"

    $libraries = Get-JsonPropertyValue -Object $deps -Name 'libraries'
    Assert-JsonObject -Value $libraries -Description 'RadarBridge.deps.json libraries'
    if (@($libraries.PSObject.Properties).Count -eq 0) { throw 'RadarBridge.deps.json is missing libraries.' }

    $requiredAssets = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $discoveredAssetCount = 0
    foreach ($libraryProperty in $selectedTarget.PSObject.Properties) {
        $library = $libraryProperty.Value
        Assert-JsonObject -Value $library -Description "RadarBridge.deps.json target library '$($libraryProperty.Name)'"
        $catalogLibrary = Get-JsonPropertyValue -Object $libraries -Name $libraryProperty.Name
        if ($null -eq $catalogLibrary -or $catalogLibrary -isnot [PSCustomObject]) {
            throw "RadarBridge.deps.json target library '$($libraryProperty.Name)' is missing from libraries."
        }

        foreach ($assetType in @('runtime', 'native', 'resources')) {
            $section = Get-JsonPropertyValue -Object $library -Name $assetType
            if ($null -eq $section) { continue }
            Assert-JsonObject -Value $section -Description "RadarBridge.deps.json library '$($libraryProperty.Name)' $assetType assets"
            foreach ($asset in $section.PSObject.Properties) {
                Assert-JsonObject -Value $asset.Value -Description "$assetType asset '$($asset.Name)' metadata"
                $relativePath = Get-PublishedAssetRelativePath -AssetPath $asset.Name -AssetType $assetType -Metadata $asset.Value
                [void]$requiredAssets.Add($relativePath)
                $discoveredAssetCount++
            }
        }

        $runtimeTargets = Get-JsonPropertyValue -Object $library -Name 'runtimeTargets'
        if ($null -ne $runtimeTargets) {
            Assert-JsonObject -Value $runtimeTargets -Description "RadarBridge.deps.json library '$($libraryProperty.Name)' runtimeTargets assets"
            foreach ($asset in $runtimeTargets.PSObject.Properties) {
                Assert-JsonObject -Value $asset.Value -Description "runtimeTargets asset '$($asset.Name)' metadata"
                $rid = [string](Get-JsonPropertyValue -Object $asset.Value -Name 'rid')
                $assetType = [string](Get-JsonPropertyValue -Object $asset.Value -Name 'assetType')
                if ([string]::IsNullOrWhiteSpace($rid) -or [string]::IsNullOrWhiteSpace($assetType)) {
                    throw "runtimeTargets asset '$($asset.Name)' must declare rid and assetType."
                }
                if ($rid -ine $ExpectedRuntimeIdentifier) { continue }
                $relativePath = Get-PublishedAssetRelativePath -AssetPath $asset.Name -AssetType $assetType -Metadata $asset.Value
                [void]$requiredAssets.Add($relativePath)
                $discoveredAssetCount++
            }
        }
    }

    if ($discoveredAssetCount -eq 0) {
        throw "RadarBridge.deps.json selected target '$targetName' contains no runtime, native, resources or runtimeTargets assets."
    }
    foreach ($relativePath in $requiredAssets) {
        $path = Join-Path $Directory $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "RadarBridge dependency asset is missing: $relativePath"
        }
    }
}

function Assert-SelfContainedRuntime {
    param([Parameter(Mandatory)] [string]$Directory)

    foreach ($name in @(
        'RadarBridge.exe', 'RadarBridge.dll', 'RadarBridge.deps.json', 'RadarBridge.runtimeconfig.json',
        'hostfxr.dll', 'hostpolicy.dll')) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required RadarBridge host file is missing: $name"
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
    Assert-DependencyManifest -Directory $Directory -ExpectedRuntimeIdentifier 'win-x64'
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

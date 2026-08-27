Set-StrictMode -Version Latest

function Get-Sha256Hash {
    param([Parameter(Mandatory)] [string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            return [System.BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

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
    if ($Object -is [System.Collections.IDictionary]) {
        if (-not $Object.ContainsKey($Name)) { return $null }
        return $Object[$Name]
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-JsonObjectEntries {
    param([Parameter(Mandatory)] [object]$Object)
    if ($Object -is [System.Collections.IDictionary]) {
        foreach ($key in $Object.Keys) {
            [PSCustomObject]@{ Name = [string]$key; Value = $Object[$key] }
        }
        return
    }
    foreach ($property in $Object.PSObject.Properties) {
        [PSCustomObject]@{ Name = $property.Name; Value = $property.Value }
    }
}

function Assert-JsonObject {
    param(
        [AllowNull()] [object]$Value,
        [Parameter(Mandatory)] [string]$Description
    )
    if ($null -eq $Value -or
        ($Value -isnot [PSCustomObject] -and $Value -isnot [System.Collections.IDictionary])) {
        throw "$Description must be an object."
    }
}

function Get-PublishedAssetRelativePath {
    param(
        [Parameter(Mandatory)] [string]$AssetPath,
        [Parameter(Mandatory)] [string]$AssetType,
        [Parameter(Mandatory)] [object]$Metadata
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

function Add-RequiredDependencyAsset {
    param(
        [Parameter(Mandatory)] [System.Collections.Generic.Dictionary[string,string]]$RequiredAssets,
        [Parameter(Mandatory)] [string]$RelativePath,
        [Parameter(Mandatory)] [string]$Provenance
    )
    if ($RequiredAssets.ContainsKey($RelativePath)) {
        $existingProvenance = $RequiredAssets[$RelativePath]
        if ($existingProvenance -ceq $Provenance) { return }
        throw "Dependency asset collision for mapped output '$RelativePath': $existingProvenance conflicts with $Provenance."
    }
    $RequiredAssets.Add($RelativePath, $Provenance)
}

function Assert-DependencyManifest {
    param(
        [Parameter(Mandatory)] [string]$Directory,
        [Parameter(Mandatory)] [string]$ExpectedRuntimeIdentifier
    )

    $depsPath = Join-Path $Directory 'RadarBridge.deps.json'
    try {
        Add-Type -AssemblyName System.Web.Extensions
        $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
        $serializer.MaxJsonLength = [int]::MaxValue
        $serializer.RecursionLimit = 256
        $deps = $serializer.DeserializeObject((Get-Content -LiteralPath $depsPath -Raw -Encoding UTF8))
    }
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
    if (@(Get-JsonObjectEntries -Object $libraries).Count -eq 0) { throw 'RadarBridge.deps.json is missing libraries.' }

    $requiredAssets = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $discoveredAssetCount = 0
    foreach ($libraryProperty in (Get-JsonObjectEntries -Object $selectedTarget)) {
        $library = $libraryProperty.Value
        Assert-JsonObject -Value $library -Description "RadarBridge.deps.json target library '$($libraryProperty.Name)'"
        $catalogLibrary = Get-JsonPropertyValue -Object $libraries -Name $libraryProperty.Name
        if ($null -eq $catalogLibrary -or
            ($catalogLibrary -isnot [PSCustomObject] -and $catalogLibrary -isnot [System.Collections.IDictionary])) {
            throw "RadarBridge.deps.json target library '$($libraryProperty.Name)' is missing from libraries."
        }

        foreach ($assetType in @('runtime', 'native', 'resources')) {
            $section = Get-JsonPropertyValue -Object $library -Name $assetType
            if ($null -eq $section) { continue }
            Assert-JsonObject -Value $section -Description "RadarBridge.deps.json library '$($libraryProperty.Name)' $assetType assets"
            foreach ($asset in (Get-JsonObjectEntries -Object $section)) {
                Assert-JsonObject -Value $asset.Value -Description "$assetType asset '$($asset.Name)' metadata"
                $relativePath = Get-PublishedAssetRelativePath -AssetPath $asset.Name -AssetType $assetType -Metadata $asset.Value
                $provenance = "library '$($libraryProperty.Name)', section '$assetType', asset '$($asset.Name)'"
                Add-RequiredDependencyAsset -RequiredAssets $requiredAssets -RelativePath $relativePath -Provenance $provenance
                $discoveredAssetCount++
            }
        }

        $runtimeTargets = Get-JsonPropertyValue -Object $library -Name 'runtimeTargets'
        if ($null -ne $runtimeTargets) {
            Assert-JsonObject -Value $runtimeTargets -Description "RadarBridge.deps.json library '$($libraryProperty.Name)' runtimeTargets assets"
            foreach ($asset in (Get-JsonObjectEntries -Object $runtimeTargets)) {
                Assert-JsonObject -Value $asset.Value -Description "runtimeTargets asset '$($asset.Name)' metadata"
                $rid = [string](Get-JsonPropertyValue -Object $asset.Value -Name 'rid')
                $assetType = [string](Get-JsonPropertyValue -Object $asset.Value -Name 'assetType')
                if ([string]::IsNullOrWhiteSpace($rid) -or [string]::IsNullOrWhiteSpace($assetType)) {
                    throw "runtimeTargets asset '$($asset.Name)' must declare rid and assetType."
                }
                if ($rid -ine $ExpectedRuntimeIdentifier) { continue }
                $relativePath = Get-PublishedAssetRelativePath -AssetPath $asset.Name -AssetType $assetType -Metadata $asset.Value
                $provenance = "library '$($libraryProperty.Name)', section 'runtimeTargets', asset '$($asset.Name)', RID '$rid', assetType '$assetType'"
                Add-RequiredDependencyAsset -RequiredAssets $requiredAssets -RelativePath $relativePath -Provenance $provenance
                $discoveredAssetCount++
            }
        }
    }

    if ($discoveredAssetCount -eq 0) {
        throw "RadarBridge.deps.json selected target '$targetName' contains no runtime, native, resources or runtimeTargets assets."
    }
    foreach ($relativePath in $requiredAssets.Keys) {
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

function Assert-InteractionBridgePayload {
    param(
        [Parameter(Mandatory)] [string]$Directory,
        [Parameter(Mandatory)] [string]$ExpectedVersion
    )

    foreach ($name in @(
        'BlazeInteractionBridge.exe',
        'BlazeInteractionBridge.dll',
        'BlazeInteractionBridge.deps.json',
        'BlazeInteractionBridge.runtimeconfig.json',
        'hostfxr.dll',
        'hostpolicy.dll',
        'bridge-version.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $name) -PathType Leaf)) {
            throw "Required BlazeInteractionBridge host file is missing: $name"
        }
    }

    $executables = @(Get-ChildItem -LiteralPath $Directory -Filter '*.exe' -File -Recurse)
    if ($executables.Count -ne 1 -or $executables[0].Name -cne 'BlazeInteractionBridge.exe') {
        throw 'Interaction payload must contain exactly one executable named BlazeInteractionBridge.exe.'
    }

    $actualVersion = (Get-Content -LiteralPath (Join-Path $Directory 'bridge-version.txt') -Raw -Encoding UTF8).Trim()
    if ($actualVersion -cne $ExpectedVersion) {
        throw "BlazeInteractionBridge version marker expected '$ExpectedVersion', found '$actualVersion'."
    }

    try {
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $Directory 'BlazeInteractionBridge.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch { throw "BlazeInteractionBridge.runtimeconfig.json is not valid JSON: $($_.Exception.Message)" }
    $optionNames = @($runtimeConfig.runtimeOptions.PSObject.Properties.Name)
    if ($optionNames -contains 'framework' -or $optionNames -contains 'frameworks' -or
        $optionNames -notcontains 'includedFrameworks') {
        throw 'BlazeInteractionBridge runtimeconfig describes framework-dependent output; embedding is forbidden.'
    }
    $frameworkNames = @($runtimeConfig.runtimeOptions.includedFrameworks | ForEach-Object { $_.name })
    foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        if ($frameworkNames -notcontains $framework) {
            throw "BlazeInteractionBridge runtimeconfig is missing included framework '$framework'."
        }
    }

    $radarDirectory = Join-Path $Directory 'Providers\Radar'
    $manifestPath = Join-Path $radarDirectory 'provider.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'The external Radar Provider manifest is missing.'
    }
    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "The external Radar Provider manifest is invalid JSON: $($_.Exception.Message)" }
    if ($manifest.id -cne 'blaze.radar.f10f20' -or
        $manifest.version -cne $ExpectedVersion -or
        $manifest.providerApiVersion -ne 1 -or
        $manifest.entryAssembly -cne 'Blaze.Provider.Radar.dll' -or
        $manifest.entryType -cne 'Blaze.Provider.Radar.RadarPlugin') {
        throw "The external Radar Provider manifest does not match Gate A Provider API 1 and version $ExpectedVersion."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $radarDirectory $manifest.entryAssembly) -PathType Leaf)) {
        throw "The external Radar Provider entry assembly is missing: $($manifest.entryAssembly)"
    }

    $cameraDirectory = Join-Path $Directory 'Providers\CameraVision'
    $cameraManifestPath = Join-Path $cameraDirectory 'provider.json'
    if (-not (Test-Path -LiteralPath $cameraManifestPath -PathType Leaf)) {
        throw 'The external CameraVision Provider manifest is missing.'
    }
    try { $cameraManifest = Get-Content -LiteralPath $cameraManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "The external CameraVision Provider manifest is invalid JSON: $($_.Exception.Message)" }
    if ($cameraManifest.id -cne 'blaze.camera.vision' -or
        $cameraManifest.version -cne $ExpectedVersion -or
        $cameraManifest.providerApiVersion -ne 1 -or
        $cameraManifest.entryAssembly -cne 'Blaze.Provider.CameraVision.dll' -or
        $cameraManifest.entryType -cne 'Blaze.Provider.CameraVision.CameraVisionPlugin') {
        throw "The external CameraVision Provider manifest does not match Provider API 1 and version $ExpectedVersion."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $cameraDirectory $cameraManifest.entryAssembly) -PathType Leaf)) {
        throw "The external CameraVision Provider entry assembly is missing: $($cameraManifest.entryAssembly)"
    }
    $cameraNativeLibraries = @(Get-ChildItem -LiteralPath $cameraDirectory -Filter 'OpenCvSharpExtern.dll' -File -Recurse)
    if ($cameraNativeLibraries.Count -ne 1) {
        throw 'The external CameraVision Provider must contain exactly one OpenCvSharpExtern.dll native runtime.'
    }
}

function Assert-CameraVisionHandRuntime {
    param([Parameter(Mandatory)] [string]$Directory)

    foreach ($entry in (Get-ChildItem -LiteralPath $Directory -Force -Recurse)) {
        foreach ($forbiddenName in @('python', 'CameraWorker', 'MediaPipeWorker', 'ProviderHost')) {
            if ($entry.Name.IndexOf($forbiddenName, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "CameraVision payload contains forbidden runtime name '$($entry.Name)'."
            }
        }
    }

    $manifestPath = Join-Path $Directory 'hand-runtime.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'The external CameraVision hand runtime manifest is missing.'
    }
    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "The external CameraVision hand runtime manifest is invalid JSON: $($_.Exception.Message)" }

    if ($manifest.schemaVersion -ne 1 -or $manifest.abiVersion -ne 1) {
        throw 'The external CameraVision hand runtime manifest must use schemaVersion 1 and ABI version 1.'
    }

    foreach ($asset in @(
        [PSCustomObject]@{
            Label = 'native'
            FileName = 'Blaze.HandTracking.Native.dll'
            RelativePath = 'runtimes/win-x64/native/Blaze.HandTracking.Native.dll'
            ManifestAsset = $manifest.nativeLibrary
        },
        [PSCustomObject]@{
            Label = 'model'
            FileName = 'hand_landmarker.task'
            RelativePath = 'models/hand_landmarker.task'
            ManifestAsset = $manifest.model
        })) {
        $matches = @(Get-ChildItem -LiteralPath $Directory -Filter $asset.FileName -File -Recurse)
        if ($matches.Count -ne 1) {
            throw "The external CameraVision hand $($asset.Label) payload must contain exactly one $($asset.FileName)."
        }
        if ($null -eq $asset.ManifestAsset -or
            [string]$asset.ManifestAsset.path -cne $asset.RelativePath -or
            [string]::IsNullOrWhiteSpace([string]$asset.ManifestAsset.sha256)) {
            throw "The external CameraVision hand $($asset.Label) manifest entry is invalid."
        }
        $expectedPath = [System.IO.Path]::GetFullPath((Join-Path $Directory $asset.RelativePath))
        if (-not [string]::Equals(
                $matches[0].FullName,
                $expectedPath,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The external CameraVision hand $($asset.Label) asset is not at '$($asset.RelativePath)'."
        }
        $actualHash = Get-Sha256Hash -Path $expectedPath
        if (-not [string]::Equals(
                $actualHash,
                [string]$asset.ManifestAsset.sha256,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The external CameraVision hand $($asset.Label) SHA-256 does not match hand-runtime.json."
        }
    }
}

function Copy-ValidatedInteractionBridgePayload {
    param(
        [Parameter(Mandatory)] [string]$SourceDirectory,
        [Parameter(Mandatory)] [string]$DestinationDirectory,
        [Parameter(Mandatory)] [string]$ExpectedDestinationDirectory,
        [Parameter(Mandatory)] [string]$ExpectedVersion
    )

    Assert-InteractionBridgePayload -Directory $SourceDirectory -ExpectedVersion $ExpectedVersion
    Remove-ValidatedDirectory -Path $DestinationDirectory -ExpectedPath $ExpectedDestinationDirectory -Label 'Interaction embedded Bridge'
    New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
    Get-ChildItem -LiteralPath $SourceDirectory | Copy-Item -Destination $DestinationDirectory -Recurse -Force
    Assert-InteractionBridgePayload -Directory $DestinationDirectory -ExpectedVersion $ExpectedVersion

    $sourceHash = Get-Sha256Hash -Path (Join-Path $SourceDirectory 'BlazeInteractionBridge.exe')
    $destinationHash = Get-Sha256Hash -Path (Join-Path $DestinationDirectory 'BlazeInteractionBridge.exe')
    if ($sourceHash -cne $destinationHash) {
        throw "BlazeInteractionBridge embedded copy SHA-256 mismatch. Source $sourceHash, destination $destinationHash."
    }
    return $destinationHash
}

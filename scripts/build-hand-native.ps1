[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repositoryRoot 'eng\mediapipe-hand.json'
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$sourceRoot = Join-Path $artifactsRoot 'native-src\mediapipe'
$checkoutMarkerPath = Join-Path $sourceRoot '.blaze-checkout-complete'
$overlayPackage = 'mediapipe/tasks/c/blaze_hand_tracking'
$overlayRoot = Join-Path $sourceRoot $overlayPackage
$toolRoot = Join-Path $artifactsRoot 'tools'
$bazeliskPath = Join-Path $toolRoot 'bazelisk-windows-amd64.exe'
$bazeliskUrl = 'https://github.com/bazelbuild/bazelisk/releases/download/v1.26.0/bazelisk-windows-amd64.exe'
$bazeliskSha256 = '023734f33ed6b9c6d65468fe20bb2c5fb32473ccb8aca2fc5bf1521e61ce1622'
$nativeOutputDirectory = Join-Path $repositoryRoot 'providers\CameraVision\Blaze.Provider.CameraVision\runtimes\win-x64\native'
$nativeOutputPath = Join-Path $nativeOutputDirectory 'Blaze.HandTracking.Native.dll'
$modelOutputDirectory = Join-Path $repositoryRoot 'providers\CameraVision\Blaze.Provider.CameraVision\models'
$modelOutputPath = Join-Path $modelOutputDirectory 'hand_landmarker.task'

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $artifactsRoot, $toolRoot | Out-Null

if (-not (Test-Path -LiteralPath $bazeliskPath -PathType Leaf) -or
    (Get-FileHash -LiteralPath $bazeliskPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $bazeliskSha256) {
    $bazeliskTemporaryPath = "$bazeliskPath.download"
    & curl.exe --fail --location --output $bazeliskTemporaryPath $bazeliskUrl
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to download Bazelisk from $bazeliskUrl."
    }
    $actualBazeliskHash = (Get-FileHash -LiteralPath $bazeliskTemporaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualBazeliskHash -ne $bazeliskSha256) {
        throw "Bazelisk SHA-256 mismatch. Expected $bazeliskSha256, actual $actualBazeliskHash."
    }
    Move-Item -LiteralPath $bazeliskTemporaryPath -Destination $bazeliskPath -Force
}

if ((Test-Path -LiteralPath (Join-Path $sourceRoot '.git') -PathType Container) -and
    -not (Test-Path -LiteralPath $checkoutMarkerPath -PathType Leaf)) {
    $resolvedSourceRoot = (Resolve-Path -LiteralPath $sourceRoot).Path
    $expectedSourceRoot = [System.IO.Path]::GetFullPath($sourceRoot)
    if ($resolvedSourceRoot -ne $expectedSourceRoot -or
        -not $resolvedSourceRoot.StartsWith((Join-Path $artifactsRoot 'native-src\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected incomplete checkout: $resolvedSourceRoot"
    }
    Remove-Item -LiteralPath $resolvedSourceRoot -Recurse -Force
}

if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot '.git') -PathType Container)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $sourceRoot) | Out-Null
    & git -c core.longpaths=true clone --filter=blob:none --depth 1 --branch $manifest.tag $manifest.repository $sourceRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to clone MediaPipe tag $($manifest.tag)."
    }
    [System.IO.File]::WriteAllText($checkoutMarkerPath, "$($manifest.commit)`r`n", [System.Text.UTF8Encoding]::new($false))
}

$actualCommit = (& git -C $sourceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -ne [string]$manifest.commit) {
    throw "MediaPipe commit mismatch. Expected $($manifest.commit), actual $actualCommit."
}

# MediaPipe v0.10.35 declares kGpuService with ABSL_CONST_INIT but omits the
# same qualifier on its definition. Current Abseil maps that macro to C++20
# constinit, so MSVC correctly rejects the mismatch. Patch only the pinned
# checkout and fail if the expected upstream source no longer matches.
$gpuServicePath = Join-Path $sourceRoot 'mediapipe\gpu\gpu_service.cc'
$gpuServiceSource = Get-Content -LiteralPath $gpuServicePath -Raw
$gpuServiceOriginal = 'const GraphService<GpuResources> kGpuService('
$gpuServicePatched = 'ABSL_CONST_INIT const GraphService<GpuResources> kGpuService('
if ($gpuServiceSource.Contains($gpuServicePatched)) {
    # Already patched; do not rewrite the file or invalidate Bazel's cache.
} elseif ($gpuServiceSource.Contains($gpuServiceOriginal)) {
    $gpuServiceSource = $gpuServiceSource.Replace($gpuServiceOriginal, $gpuServicePatched)
    [System.IO.File]::WriteAllText($gpuServicePath, $gpuServiceSource, [System.Text.UTF8Encoding]::new($false))
} else {
    throw "MediaPipe gpu_service.cc does not match the pinned compatibility patch."
}

# MSVC 14.51 resolves the unqualified friend declaration below to the
# non-template mediapipe::SubgraphContext from the parent namespace. An
# explicit api3 forward declaration makes the intended template unambiguous.
$api3GraphPath = Join-Path $sourceRoot 'mediapipe\framework\api3\graph.h'
$api3GraphSource = Get-Content -LiteralPath $api3GraphPath -Raw
$api3GraphOriginal = "namespace mediapipe::api3 {`n`n// ``Graph`` must be used"
$api3GraphPatched = "namespace mediapipe::api3 {`n`ntemplate <typename NodeT>`nclass SubgraphContext;`n`n// ``Graph`` must be used"
if ($api3GraphSource.Contains($api3GraphOriginal)) {
    $api3GraphSource = $api3GraphSource.Replace($api3GraphOriginal, $api3GraphPatched)
    [System.IO.File]::WriteAllText($api3GraphPath, $api3GraphSource, [System.Text.UTF8Encoding]::new($false))
} elseif (-not $api3GraphSource.Contains("template <typename NodeT>`nclass SubgraphContext;")) {
    throw "MediaPipe api3 graph.h does not match the pinned MSVC compatibility patch."
}

# The DoNotSpecify packs are useful on the public Visit API, but are not part
# of the internal recursive helpers' behavior. MSVC 14.51 tries to bind the
# explicitly supplied payload types to those non-type packs during recursion.
$calculatorContextPath = Join-Path $sourceRoot 'mediapipe\framework\api3\calculator_context.h'
$calculatorContextSource = Get-Content -LiteralPath $calculatorContextPath -Raw
$singlePayloadOriginal = 'template <typename T, int&... DoNotSpecify, typename F>'
$singlePayloadPatched = 'template <typename T, typename F>'
$multiplePayloadOriginal = "template <typename T, typename U, typename... Rest, int&... DoNotSpecify,`n          typename F>"
$multiplePayloadPatched = 'template <typename T, typename U, typename... Rest, typename F>'
$singlePayloadCount = [regex]::Matches($calculatorContextSource, [regex]::Escape($singlePayloadOriginal)).Count
$multiplePayloadCount = [regex]::Matches($calculatorContextSource, [regex]::Escape($multiplePayloadOriginal)).Count
if ($singlePayloadCount -eq 2 -and $multiplePayloadCount -eq 2) {
    $calculatorContextSource = $calculatorContextSource.Replace($singlePayloadOriginal, $singlePayloadPatched)
    $calculatorContextSource = $calculatorContextSource.Replace($multiplePayloadOriginal, $multiplePayloadPatched)
    [System.IO.File]::WriteAllText($calculatorContextPath, $calculatorContextSource, [System.Text.UTF8Encoding]::new($false))
} elseif ($calculatorContextSource.Contains($singlePayloadOriginal) -or
          $calculatorContextSource.Contains($multiplePayloadOriginal) -or
          [regex]::Matches($calculatorContextSource, [regex]::Escape($singlePayloadPatched)).Count -lt 2 -or
          [regex]::Matches($calculatorContextSource, [regex]::Escape($multiplePayloadPatched)).Count -lt 2) {
    throw "MediaPipe calculator_context.h does not match the pinned MSVC compatibility patch."
}

# MSVC 14.51 cannot use absl::string_view's internal pointer as part of the
# CompileTimeString non-type template argument. A constexpr character array
# selects CompileTimeString's equivalent owning-array constructor instead.
$imageToTensorPath = Join-Path $sourceRoot 'mediapipe\calculators\tensor\image_to_tensor_calculator.h'
$imageToTensorSource = Get-Content -LiteralPath $imageToTensorPath -Raw
$imageToTensorOriginal = 'inline constexpr absl::string_view kImageToTensorNodeName ='
$imageToTensorPatched = 'inline constexpr char kImageToTensorNodeName[] ='
if ($imageToTensorSource.Contains($imageToTensorOriginal)) {
    $imageToTensorSource = $imageToTensorSource.Replace($imageToTensorOriginal, $imageToTensorPatched)
    [System.IO.File]::WriteAllText($imageToTensorPath, $imageToTensorSource, [System.Text.UTF8Encoding]::new($false))
} elseif (-not $imageToTensorSource.Contains($imageToTensorPatched)) {
    throw "MediaPipe image_to_tensor_calculator.h does not match the pinned MSVC compatibility patch."
}

# The FrameBuffer CPU converter selected by MEDIAPIPE_ENABLE_HALIDE rejects
# BORDER_ZERO at runtime (the pinned MediaPipe source marks it as a TODO). The
# hand detector is the only hand pipeline stage requesting that mode; later
# landmark ROIs already use BORDER_REPLICATE. Select the converter's supported
# border behavior so the Windows CPU graph can run without an OpenCV runtime.
$handDetectorGraphPath = Join-Path $sourceRoot 'mediapipe\tasks\cc\vision\hand_detector\hand_detector_graph.cc'
$handDetectorGraphSource = Get-Content -LiteralPath $handDetectorGraphPath -Raw
$handDetectorBorderOriginal = 'mediapipe::ImageToTensorCalculatorOptions::BORDER_ZERO);'
$handDetectorBorderPatched = 'mediapipe::ImageToTensorCalculatorOptions::BORDER_REPLICATE);'
if ($handDetectorGraphSource.Contains($handDetectorBorderOriginal)) {
    $handDetectorGraphSource = $handDetectorGraphSource.Replace(
        $handDetectorBorderOriginal,
        $handDetectorBorderPatched)
    [System.IO.File]::WriteAllText($handDetectorGraphPath, $handDetectorGraphSource, [System.Text.UTF8Encoding]::new($false))
} elseif (-not $handDetectorGraphSource.Contains($handDetectorBorderPatched)) {
    throw "MediaPipe hand detector graph does not match the pinned FrameBuffer compatibility patch."
}

$frameBufferRotationPatchPath = Join-Path $repositoryRoot 'native\Blaze.HandTracking.Native\patches\mediapipe-frame-buffer-arbitrary-rotation.patch'
$savedErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    & git -C $sourceRoot apply --reverse --check $frameBufferRotationPatchPath 2>$null
    $rotationPatchAlreadyApplied = $LASTEXITCODE -eq 0
} finally {
    $ErrorActionPreference = $savedErrorActionPreference
}
if (-not $rotationPatchAlreadyApplied) {
    try {
        $ErrorActionPreference = 'Continue'
        & git -C $sourceRoot apply --check $frameBufferRotationPatchPath 2>$null
        $rotationPatchMatches = $LASTEXITCODE -eq 0
    } finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    if (-not $rotationPatchMatches) {
        throw 'MediaPipe FrameBuffer arbitrary-rotation patch does not match the pinned checkout.'
    }
    & git -C $sourceRoot apply $frameBufferRotationPatchPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to apply the MediaPipe FrameBuffer arbitrary-rotation patch.'
    }
}

$frameBufferRoiPatchPath = Join-Path $repositoryRoot 'native\Blaze.HandTracking.Native\patches\mediapipe-frame-buffer-out-of-bounds-roi.patch'
try {
    $ErrorActionPreference = 'Continue'
    & git -C $sourceRoot apply --reverse --check $frameBufferRoiPatchPath 2>$null
    $roiPatchAlreadyApplied = $LASTEXITCODE -eq 0
} finally {
    $ErrorActionPreference = $savedErrorActionPreference
}
if (-not $roiPatchAlreadyApplied) {
    try {
        $ErrorActionPreference = 'Continue'
        & git -C $sourceRoot apply --check $frameBufferRoiPatchPath 2>$null
        $roiPatchMatches = $LASTEXITCODE -eq 0
    } finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    if (-not $roiPatchMatches) {
        throw 'MediaPipe FrameBuffer out-of-bounds ROI patch does not match the pinned checkout.'
    }
    & git -C $sourceRoot apply $frameBufferRoiPatchPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to apply the MediaPipe FrameBuffer out-of-bounds ROI patch.'
    }
}

# MediaPipe's Halide rule puts GCC -Wno-* options in the platform-common list.
# Bazel translates them to invalid /W... values for MSVC, so remove only those
# warning-suppression entries in this Windows-only pinned build checkout.
$halideRulesPath = Join-Path $sourceRoot 'third_party\halide\halide.bzl'
$halideRulesSource = Get-Content -LiteralPath $halideRulesPath -Raw
$halideRulesOriginalSource = $halideRulesSource
foreach ($unsupportedWarningOption in @('        "-Wno-conversion",' + "`n", '        "-Wno-sign-compare",' + "`n")) {
    if ($halideRulesSource.Contains($unsupportedWarningOption)) {
        $halideRulesSource = $halideRulesSource.Replace($unsupportedWarningOption, '')
    }
}

# Halide's generator executables dynamically load Halide.dll on Windows. The
# pinned rule supplies a small explicit environment dictionary, which prevents
# Bazel's action PATH (including the fetched Halide runtime directory) from
# reaching the generator process. Preserve that dictionary while also
# inheriting the action environment configured below.
$halideRunOriginalPattern = '        env = env,\r?\n        executable = generator_binary\.files_to_run\.executable,'
$halideRunPatchedPattern = '        env = env,\r?\n        use_default_shell_env = True,\r?\n        executable = generator_binary\.files_to_run\.executable,'
if ([regex]::IsMatch($halideRulesSource, $halideRunOriginalPattern)) {
    $halideRulesSource = [regex]::Replace(
        $halideRulesSource,
        $halideRunOriginalPattern,
        "        env = env,`r`n        use_default_shell_env = True,`r`n        executable = generator_binary.files_to_run.executable,",
        1)
} elseif (-not [regex]::IsMatch($halideRulesSource, $halideRunPatchedPattern)) {
    throw "MediaPipe Halide generator action does not match the pinned Windows PATH compatibility patch."
}
if ($halideRulesSource.Contains('"-Wno-conversion"') -or
    $halideRulesSource.Contains('"-Wno-sign-compare"')) {
    throw "MediaPipe Halide rules do not match the pinned MSVC compatibility patch."
}
if ($halideRulesSource -ne $halideRulesOriginalSource) {
    [System.IO.File]::WriteAllText($halideRulesPath, $halideRulesSource, [System.Text.UTF8Encoding]::new($false))
}

New-Item -ItemType Directory -Force -Path $overlayRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'native\Blaze.HandTracking.Native\BUILD.bazel') -Destination (Join-Path $overlayRoot 'BUILD.bazel') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'native\Blaze.HandTracking.Native\blaze_hand_tracking.h') -Destination (Join-Path $overlayRoot 'blaze_hand_tracking.h') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'native\Blaze.HandTracking.Native\blaze_hand_tracking.cc') -Destination (Join-Path $overlayRoot 'blaze_hand_tracking.cc') -Force

$bashPath = 'C:\Program Files\Git\usr\bin\bash.exe'
if (-not (Test-Path -LiteralPath $bashPath -PathType Leaf)) {
    throw "Git Bash was not found at $bashPath."
}

$visualStudioRoot = 'D:\Microsoft Visual Studio'
$visualStudioVc = Join-Path $visualStudioRoot 'VC'
if (-not (Test-Path -LiteralPath $visualStudioVc -PathType Container)) {
    throw "Visual Studio C++ tools were not found at $visualStudioVc."
}

$pythonExecutable = (& py -3 -c 'import sys; print(sys.executable)').Trim()
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $pythonExecutable -PathType Leaf)) {
    throw 'Python 3 could not be resolved through the Windows py launcher.'
}
$pythonDirectory = Split-Path -Parent $pythonExecutable

$previousEnvironment = @{
    BAZEL_SH = $env:BAZEL_SH
    BAZEL_VS = $env:BAZEL_VS
    BAZEL_VC = $env:BAZEL_VC
    BAZELISK_HOME = $env:BAZELISK_HOME
    USE_BAZEL_VERSION = $env:USE_BAZEL_VERSION
    Path = $env:Path
}

try {
    $env:BAZEL_SH = $bashPath
    $env:BAZEL_VS = $visualStudioRoot
    $env:BAZEL_VC = $visualStudioVc
    $env:BAZELISK_HOME = Join-Path $artifactsRoot 'bazelisk-home'
    $env:USE_BAZEL_VERSION = '7.4.1'
    $env:Path = "$pythonDirectory;$($env:Path)"
    # cl.exe still rejects response-file paths beyond MAX_PATH. Keep Bazel's
    # execution root short even when this repository lives in a deep worktree.
    $repositoryDriveRoot = [System.IO.Path]::GetPathRoot($repositoryRoot)
    $bazelUserRoot = Join-Path $repositoryDriveRoot 'bh'

    Push-Location $sourceRoot
    try {
        $outputBaseLines = & $bazeliskPath "--output_user_root=$bazelUserRoot" info output_base
        if ($LASTEXITCODE -ne 0) {
            throw "Bazel failed to resolve its output base (exit $LASTEXITCODE)."
        }
        $outputBase = [string]($outputBaseLines | Select-Object -Last 1)
        $halideRuntimeDirectory = Join-Path $outputBase 'external\windows_halide\bin\Release'
        $halideRuntimePath = Join-Path $halideRuntimeDirectory 'Halide.dll'
        if (-not (Test-Path -LiteralPath $halideRuntimePath -PathType Leaf)) {
            & $bazeliskPath `
                "--output_user_root=$bazelUserRoot" `
                fetch `
                --define MEDIAPIPE_DISABLE_GPU=1 `
                --define MEDIAPIPE_DISABLE_OPENCV=1 `
                --define MEDIAPIPE_ENABLE_HALIDE=1 `
                --repo_env=HERMETIC_PYTHON_VERSION=3.11 `
                "--repo_env=PATH=$($env:Path)" `
                //mediapipe/tasks/c/blaze_hand_tracking:Blaze.HandTracking.Native.dll
            if ($LASTEXITCODE -ne 0) {
                throw "Bazel failed to fetch MediaPipe/Halide dependencies (exit $LASTEXITCODE)."
            }
        }
        if (-not (Test-Path -LiteralPath $halideRuntimePath -PathType Leaf)) {
            throw "The pinned Windows Halide runtime was not found after fetch: $halideRuntimePath"
        }
        $gitUtilitiesDirectory = Split-Path -Parent $bashPath
        $actionPath = "$halideRuntimeDirectory;$gitUtilitiesDirectory;$($env:Path)"
        $env:Path = $actionPath

        & $bazeliskPath `
            "--output_user_root=$bazelUserRoot" `
            build `
            -c opt `
            --strip=always `
            --define MEDIAPIPE_DISABLE_GPU=1 `
            --define MEDIAPIPE_DISABLE_OPENCV=1 `
            --define MEDIAPIPE_ENABLE_HALIDE=1 `
            --repo_env=HERMETIC_PYTHON_VERSION=3.11 `
            "--repo_env=PATH=$($env:Path)" `
            --action_env=PATH `
            --conlyopt=/std:c11 `
            --conlyopt=/experimental:c11atomics `
            --cxxopt=/std:c++20 `
            --cxxopt=/Zc:preprocessor `
            --cxxopt=/utf-8 `
            --host_cxxopt=/std:c++20 `
            --host_cxxopt=/Zc:preprocessor `
            --host_cxxopt=/utf-8 `
            --enable_runfiles=no `
            //mediapipe/tasks/c/blaze_hand_tracking:Blaze.HandTracking.Native.dll
        if ($LASTEXITCODE -ne 0) {
            throw "Bazel failed to build Blaze.HandTracking.Native.dll (exit $LASTEXITCODE)."
        }
    } finally {
        Pop-Location
    }
} finally {
    foreach ($entry in $previousEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}

$builtDllPath = Join-Path $sourceRoot 'bazel-bin\mediapipe\tasks\c\blaze_hand_tracking\Blaze.HandTracking.Native.dll'
if (-not (Test-Path -LiteralPath $builtDllPath -PathType Leaf)) {
    throw "Bazel reported success but the DLL was not found: $builtDllPath"
}

New-Item -ItemType Directory -Force -Path $nativeOutputDirectory | Out-Null
Copy-Item -LiteralPath $builtDllPath -Destination $nativeOutputPath -Force

New-Item -ItemType Directory -Force -Path $modelOutputDirectory | Out-Null
$actualModelHash = if (Test-Path -LiteralPath $modelOutputPath -PathType Leaf) {
    (Get-FileHash -LiteralPath $modelOutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
} else {
    ''
}
if ($actualModelHash -ne [string]$manifest.modelSha256) {
    $modelTemporaryPath = "$modelOutputPath.download"
    & curl.exe --fail --location --output $modelTemporaryPath $manifest.modelUrl
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to download the hand landmarker model from $($manifest.modelUrl)."
    }
    $actualModelHash = (Get-FileHash -LiteralPath $modelTemporaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualModelHash -ne [string]$manifest.modelSha256) {
        throw "Hand landmarker model SHA-256 mismatch. Expected $($manifest.modelSha256), actual $actualModelHash."
    }
    Move-Item -LiteralPath $modelTemporaryPath -Destination $modelOutputPath -Force
}

$nativeDllHash = (Get-FileHash -LiteralPath $nativeOutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest | Add-Member -NotePropertyName nativeDllSha256 -NotePropertyValue $nativeDllHash -Force
$manifestJson = $manifest | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($manifestPath, "$manifestJson`r`n", [System.Text.UTF8Encoding]::new($false))

$verifyScriptPath = Join-Path $PSScriptRoot 'verify-hand-native.ps1'
& $verifyScriptPath
if ($LASTEXITCODE -ne 0) {
    throw "Native hand verification failed (exit $LASTEXITCODE)."
}

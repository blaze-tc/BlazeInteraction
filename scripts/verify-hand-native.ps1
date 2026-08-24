[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repositoryRoot 'eng\mediapipe-hand.json'
$nativeDllPath = Join-Path $repositoryRoot 'providers\CameraVision\Blaze.Provider.CameraVision\runtimes\win-x64\native\Blaze.HandTracking.Native.dll'
$modelPath = Join-Path $repositoryRoot 'providers\CameraVision\Blaze.Provider.CameraVision\models\hand_landmarker.task'

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "MediaPipe provenance manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if (-not (Test-Path -LiteralPath $nativeDllPath -PathType Leaf)) {
    throw "Native hand library is missing: $nativeDllPath"
}

if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf)) {
    throw "Hand landmarker model is missing: $modelPath"
}

$modelHash = (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($modelHash -ne [string]$manifest.modelSha256) {
    throw "Hand landmarker model SHA-256 mismatch. Expected $($manifest.modelSha256), actual $modelHash."
}

$nativeHash = (Get-FileHash -LiteralPath $nativeDllPath -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not ($manifest.PSObject.Properties.Name -contains 'nativeDllSha256')) {
    throw 'MediaPipe provenance manifest does not contain nativeDllSha256.'
}
if ($nativeHash -ne [string]$manifest.nativeDllSha256) {
    throw "Native hand library SHA-256 mismatch. Expected $($manifest.nativeDllSha256), actual $nativeHash."
}

$dumpbin = Get-Command 'dumpbin.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $dumpbin) {
    $visualStudioRoots = @(
        'D:\Microsoft Visual Studio',
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) }

    foreach ($root in $visualStudioRoots) {
        $candidate = Get-ChildItem -LiteralPath $root -Filter 'dumpbin.exe' -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\Hostx64\\x64\\dumpbin\.exe$' } |
            Sort-Object -Property FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            $dumpbin = $candidate
            break
        }
    }
}

if ($null -eq $dumpbin) {
    throw 'dumpbin.exe was not found; install the Visual Studio C++ build tools or run from a Developer PowerShell.'
}

$dumpbinPath = if ($dumpbin -is [System.Management.Automation.CommandInfo]) {
    $dumpbin.Source
} else {
    $dumpbin.FullName
}
$exports = & $dumpbinPath /nologo /exports $nativeDllPath 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "dumpbin failed while reading native exports:`n$exports"
}

$requiredExports = @(
    'blaze_hand_get_abi_version',
    'blaze_hand_create',
    'blaze_hand_process_frame',
    'blaze_hand_get_hand_count',
    'blaze_hand_copy_hand',
    'blaze_hand_destroy',
    'blaze_hand_get_last_error'
)

foreach ($requiredExport in $requiredExports) {
    if ($exports -notmatch "(?m)\b$([regex]::Escape($requiredExport))\b") {
        throw "Native hand library does not export '$requiredExport'."
    }
}

Write-Host "MediaPipe commit: $($manifest.commit)"
Write-Host "Native DLL SHA256: $nativeHash"
Write-Host "Model SHA256: $modelHash"
Write-Host "Native ABI exports: $($requiredExports.Count)/$($requiredExports.Count)"

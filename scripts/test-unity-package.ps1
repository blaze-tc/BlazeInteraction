[CmdletBinding()]
param(
    [Parameter()]
    [string]$UnityEditor,

    [Parameter()]
    [ValidateSet("EditMode", "PlayMode", "All")]
    [string]$TestPlatform = "EditMode",

    [Parameter()]
    [switch]$IncludeSamples
)

$ErrorActionPreference = "Stop"

function Resolve-UnityEditor {
    param([string]$RequestedEditor)

    if ([string]::IsNullOrWhiteSpace($RequestedEditor)) {
        $hubRoot = "C:\Program Files\Unity\Hub\Editor"
        if (-not (Test-Path -LiteralPath $hubRoot -PathType Container)) {
            throw "Unity Hub editor directory was not found at '$hubRoot'. Install a Unity 2021.3 editor or pass -UnityEditor <path>."
        }

        $candidate = Get-ChildItem -LiteralPath $hubRoot -Directory |
            Where-Object { $_.Name -match '^2021\.3\.' } |
            Sort-Object { [version](($_.Name -replace '[^0-9.]', '').TrimEnd('.')) } -Descending |
            ForEach-Object { Join-Path $_.FullName "Editor\Unity.exe" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1

        if ([string]::IsNullOrWhiteSpace($candidate)) {
            throw "No Unity 2021.3 editor was found under '$hubRoot'. Install Unity 2021.3 or pass -UnityEditor <path>."
        }

        $RequestedEditor = $candidate
    }

    if (-not (Test-Path -LiteralPath $RequestedEditor -PathType Leaf)) {
        throw "Unity editor executable was not found: '$RequestedEditor'."
    }

    $resolvedEditor = (Resolve-Path -LiteralPath $RequestedEditor).Path
    $editorDirectory = Split-Path -Parent $resolvedEditor
    $versionDirectory = Split-Path -Parent $editorDirectory
    $editorVersion = Split-Path -Leaf $versionDirectory
    if ($editorVersion -notmatch '^2021\.3\.') {
        throw "Unity 2021.3 is required, but '$resolvedEditor' resolves to editor version '$editorVersion'."
    }

    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedEditor).ProductVersion
    if (-not [string]::IsNullOrWhiteSpace($fileVersion) -and $fileVersion -notmatch '^2021\.3\.') {
        throw "Unity 2021.3 is required, but '$resolvedEditor' reports product version '$fileVersion'."
    }

    return [pscustomobject]@{
        Path = $resolvedEditor
        Version = $editorVersion
    }
}

function Assert-SafeTemporaryProjectPath {
    param(
        [string]$RepositoryRoot,
        [string]$TemporaryProject
    )

    $expected = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot "tmp\unity-package-tests"))
    $actual = [System.IO.Path]::GetFullPath($TemporaryProject)
    if (-not [string]::Equals($actual, $expected, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to recreate unexpected Unity test project path '$actual'. Expected exactly '$expected'."
    }
}

function Assert-UnityTestResults {
    param(
        [string]$ResultPath,
        [string]$LogPath,
        [string]$Platform,
        [int]$ExitCode
    )

    if ($ExitCode -ne 0) {
        throw "Unity $Platform tests exited with code $ExitCode. Result: '$ResultPath'. Log: '$LogPath'."
    }

    if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {
        throw "Unity $Platform did not produce a test result XML file at '$ResultPath'. Log: '$LogPath'."
    }

    try {
        [xml]$results = Get-Content -LiteralPath $ResultPath -Raw
    }
    catch {
        throw "Unity $Platform produced malformed test result XML at '$ResultPath': $($_.Exception.Message)"
    }

    $root = $results.DocumentElement
    if ($null -eq $root) {
        throw "Unity $Platform test result XML has no root element: '$ResultPath'."
    }

    $total = [int]($root.GetAttribute("total"))
    $failed = [int]($root.GetAttribute("failed"))
    $inconclusive = [int]($root.GetAttribute("inconclusive"))
    $passed = [int]($root.GetAttribute("passed"))
    if ($total -le 0) {
        throw "Unity $Platform discovered no tests. Result: '$ResultPath'. Log: '$LogPath'."
    }

    if ($failed -gt 0 -or $inconclusive -gt 0) {
        throw "Unity $Platform tests were not clean: passed=$passed, failed=$failed, inconclusive=$inconclusive. Result: '$ResultPath'."
    }

    if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        throw "Unity $Platform did not produce a log file at '$LogPath'."
    }

    $compileErrors = Select-String -LiteralPath $LogPath -Pattern 'error CS\d+' -CaseSensitive:$false
    if ($compileErrors) {
        throw "Unity $Platform log contains C# compiler errors. Log: '$LogPath'."
    }

    Write-Host "Unity $Platform PASS: passed=$passed, failed=$failed, inconclusive=$inconclusive"
    Write-Host "Result XML: $ResultPath"
    Write-Host "Unity log: $LogPath"
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$packageRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "UnityPackage\com.blaze.radar"))
$temporaryProject = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "tmp\unity-package-tests"))
Assert-SafeTemporaryProjectPath -RepositoryRoot $repositoryRoot -TemporaryProject $temporaryProject

$editor = Resolve-UnityEditor -RequestedEditor $UnityEditor
Write-Host "Using Unity $($editor.Version): $($editor.Path)"

if (Test-Path -LiteralPath $temporaryProject) {
    Remove-Item -LiteralPath $temporaryProject -Recurse -Force
}

$packagesDirectory = Join-Path $temporaryProject "Packages"
$projectSettingsDirectory = Join-Path $temporaryProject "ProjectSettings"
$assetsDirectory = Join-Path $temporaryProject "Assets"
$resultsDirectory = Join-Path $temporaryProject "TestResults"
New-Item -ItemType Directory -Path $packagesDirectory, $projectSettingsDirectory, $assetsDirectory, $resultsDirectory -Force | Out-Null

$manifest = @'
{
  "dependencies": {
    "com.blaze.radar": "file:../../../UnityPackage/com.blaze.radar",
    "com.unity.test-framework": "1.1.33",
    "com.unity.ugui": "1.0.0",
    "com.unity.nuget.newtonsoft-json": "3.0.2"
  }
}
'@
Set-Content -LiteralPath (Join-Path $packagesDirectory "manifest.json") -Value $manifest -Encoding UTF8
Set-Content -LiteralPath (Join-Path $projectSettingsDirectory "ProjectVersion.txt") -Value "m_EditorVersion: $($editor.Version)" -Encoding UTF8

if ($IncludeSamples) {
    $packageJson = Get-Content -LiteralPath (Join-Path $packageRoot "package.json") -Raw | ConvertFrom-Json
    $sampleRoot = Join-Path $assetsDirectory ("Samples\Blaze Radar SDK\" + $packageJson.version)
    foreach ($sample in @($packageJson.samples)) {
        $source = [System.IO.Path]::GetFullPath((Join-Path $packageRoot $sample.path))
        if (-not (Test-Path -LiteralPath $source -PathType Container)) {
            throw "Declared package sample directory was not found: '$source'."
        }

        $stableFolder = Split-Path -Leaf $source
        $destinationName = if ([string]::IsNullOrWhiteSpace($sample.displayName)) { $stableFolder } else { $sample.displayName }
        $destination = Join-Path $sampleRoot $destinationName
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -Path (Join-Path $source "*") -Destination $destination -Recurse -Force
    }
}

$platforms = if ($TestPlatform -eq "All") { @("EditMode", "PlayMode") } else { @($TestPlatform) }
foreach ($platform in $platforms) {
    $platformSlug = $platform.ToLowerInvariant()
    $resultPath = [System.IO.Path]::GetFullPath((Join-Path $resultsDirectory "$platformSlug-results.xml"))
    $logPath = [System.IO.Path]::GetFullPath((Join-Path $resultsDirectory "$platformSlug-unity.log"))
    $arguments = @(
        "-batchmode",
        "-nographics",
        "-projectPath", $temporaryProject,
        "-runTests",
        "-testPlatform", $platform,
        "-testResults", $resultPath,
        "-logFile", $logPath,
        "-quit"
    )

    $editorPath = $editor.Path
    & $editorPath @arguments
    Assert-UnityTestResults -ResultPath $resultPath -LogPath $logPath -Platform $platform -ExitCode $LASTEXITCODE
}

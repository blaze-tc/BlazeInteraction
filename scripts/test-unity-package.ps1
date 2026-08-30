[CmdletBinding()]
param(
    [Parameter()]
    [string]$UnityEditor,

    [Parameter()]
    [ValidateSet("EditMode", "PlayMode", "All")]
    [string]$TestPlatform = "EditMode",

    [Parameter()]
    [switch]$IncludeSamples,

    [Parameter(DontShow = $true)]
    [switch]$RunnerSelfTest
)

$ErrorActionPreference = "Stop"

function Parse-UnityVersion {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    $pattern = '(?<!\d)(?<major>\d{4})\.(?<minor>\d+)\.(?<patch>\d+)(?<channel>[abfp])(?<release>\d+)(?:(?<suffixLetter>[a-z])(?<suffixNumber>\d+))?'
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $Text,
        $pattern,
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant -bor
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        return $null
    }

    $channel = $match.Groups["channel"].Value.ToLowerInvariant()
    $channelRank = switch ($channel) {
        "a" { 0 }
        "b" { 1 }
        "f" { 2 }
        "p" { 3 }
        default { -1 }
    }
    $suffixLetter = $match.Groups["suffixLetter"].Value.ToLowerInvariant()
    $suffixRank = if ([string]::IsNullOrEmpty($suffixLetter)) {
        0
    }
    else {
        [int][char]$suffixLetter[0]
    }
    $suffixNumber = if ($match.Groups["suffixNumber"].Success) {
        [int]$match.Groups["suffixNumber"].Value
    }
    else {
        0
    }

    return [pscustomobject]@{
        Version = $match.Value
        Major = [int]$match.Groups["major"].Value
        Minor = [int]$match.Groups["minor"].Value
        Patch = [int]$match.Groups["patch"].Value
        ChannelRank = $channelRank
        Release = [int]$match.Groups["release"].Value
        SuffixRank = $suffixRank
        SuffixNumber = $suffixNumber
    }
}

function Sort-UnityVersionCandidates {
    param([object[]]$Candidates)

    return @($Candidates | Sort-Object -Property @(
        @{ Expression = "Patch"; Descending = $true },
        @{ Expression = "ChannelRank"; Descending = $true },
        @{ Expression = "Release"; Descending = $true },
        @{ Expression = "SuffixRank"; Descending = $true },
        @{ Expression = "SuffixNumber"; Descending = $true }
    ))
}

function Assert-SampleAssembliesCopied {
    param([string]$SampleRoot)

    $expectedAssemblies = @(
        (Join-Path $SampleRoot "Basic Interaction\Blaze.Interaction.Sample.BasicInteraction.asmdef"),
        (Join-Path $SampleRoot "Multi-Surface Routing\Blaze.Interaction.Sample.MultiSurfaceRouting.asmdef")
    )
    foreach ($assemblyPath in $expectedAssemblies) {
        if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
            throw "Included sample assembly definition was not copied: '$assemblyPath'."
        }
    }

    Write-Host "Included sample assemblies verified: $($expectedAssemblies.Count)"
}

function Find-VersionDirectoryFallback {
    param([string]$EditorPath)

    $directory = [System.IO.DirectoryInfo](Split-Path -Parent $EditorPath)
    for ($depth = 0; $depth -lt 5 -and $null -ne $directory; $depth++) {
        $parsed = Parse-UnityVersion -Text $directory.Name
        if ($null -ne $parsed -and
            [string]::Equals($parsed.Version, $directory.Name, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $parsed
        }

        $directory = $directory.Parent
    }

    return $null
}

function Select-UnityEditorVersion {
    param(
        [string]$EditorPath,
        [string]$ProductVersion,
        [object]$DirectoryVersion,
        [bool]$ExplicitlyRequested
    )

    $productVersionInfo = Parse-UnityVersion -Text $ProductVersion
    if ($ExplicitlyRequested -and $null -eq $productVersionInfo) {
        throw "Explicitly provided Unity editor '$EditorPath' must expose a parseable ProductVersion; directory fallback is disabled for explicit paths."
    }

    $selectedVersion = if ($null -ne $productVersionInfo) {
        $productVersionInfo
    }
    else {
        $DirectoryVersion
    }
    $versionSource = if ($null -ne $productVersionInfo) {
        "ProductVersion '$ProductVersion'"
    }
    else {
        "automatic Hub version directory"
    }

    if ($null -eq $selectedVersion) {
        throw "Unity 2021.3 is required, but '$EditorPath' exposes no parseable ProductVersion or automatic Hub version directory."
    }

    if ($selectedVersion.Major -ne 2021 -or $selectedVersion.Minor -ne 3) {
        throw "Unity 2021.3 is required, but '$EditorPath' resolves to '$($selectedVersion.Version)' from $versionSource."
    }

    return $selectedVersion
}

function Resolve-UnityEditor {
    param([string]$RequestedEditor)

    $explicitlyRequested = -not [string]::IsNullOrWhiteSpace($RequestedEditor)
    if (-not $explicitlyRequested) {
        $hubRoot = "C:\Program Files\Unity\Hub\Editor"
        if (-not (Test-Path -LiteralPath $hubRoot -PathType Container)) {
            throw "Unity Hub editor directory was not found at '$hubRoot'. Install a Unity 2021.3 editor or pass -UnityEditor <path>."
        }

        $candidates = foreach ($versionDirectory in Get-ChildItem -LiteralPath $hubRoot -Directory) {
            $parsed = Parse-UnityVersion -Text $versionDirectory.Name
            $executable = Join-Path $versionDirectory.FullName "Editor\Unity.exe"
            if ($null -ne $parsed -and
                [string]::Equals($parsed.Version, $versionDirectory.Name, [System.StringComparison]::OrdinalIgnoreCase) -and
                $parsed.Major -eq 2021 -and
                $parsed.Minor -eq 3 -and
                (Test-Path -LiteralPath $executable -PathType Leaf)) {
                Add-Member -InputObject $parsed -NotePropertyName Path -NotePropertyValue $executable
                $parsed
            }
        }

        $candidate = Sort-UnityVersionCandidates -Candidates @($candidates) | Select-Object -First 1

        if ($null -eq $candidate) {
            throw "No Unity 2021.3 editor was found under '$hubRoot'. Install Unity 2021.3 or pass -UnityEditor <path>."
        }

        $RequestedEditor = $candidate.Path
    }

    if (-not (Test-Path -LiteralPath $RequestedEditor -PathType Leaf)) {
        throw "Unity editor executable was not found: '$RequestedEditor'."
    }

    $resolvedEditor = (Resolve-Path -LiteralPath $RequestedEditor).Path
    $productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedEditor).ProductVersion
    $directoryVersion = if ($explicitlyRequested) {
        $null
    }
    else {
        Find-VersionDirectoryFallback -EditorPath $resolvedEditor
    }
    $parsedVersion = Select-UnityEditorVersion `
        -EditorPath $resolvedEditor `
        -ProductVersion $productVersion `
        -DirectoryVersion $directoryVersion `
        -ExplicitlyRequested $explicitlyRequested

    return [pscustomobject]@{
        Path = $resolvedEditor
        Version = $parsedVersion.Version
    }
}

function Invoke-RunnerSelfTest {
    $knownVersions = @(
        (Parse-UnityVersion -Text "2021.3.9f10")
        (Parse-UnityVersion -Text "2021.3.45f1c1")
        (Parse-UnityVersion -Text "2021.3.46f1")
    )
    if ($knownVersions -contains $null) {
        throw "Runner self-test failed: a known Unity version could not be parsed."
    }

    $sorted = Sort-UnityVersionCandidates -Candidates $knownVersions
    $actual = @($sorted | ForEach-Object { $_.Version }) -join ","
    $expected = "2021.3.46f1,2021.3.45f1c1,2021.3.9f10"
    if (-not [string]::Equals($actual, $expected, [System.StringComparison]::Ordinal)) {
        throw "Runner self-test failed: expected '$expected', found '$actual'."
    }

    $fallbackVersion = Parse-UnityVersion -Text "2021.3.45f1c1"
    $explicitVersion = Select-UnityEditorVersion `
        -EditorPath "X:\Arbitrary\Unity.exe" `
        -ProductVersion "2021.3.46f1 (test)" `
        -DirectoryVersion $null `
        -ExplicitlyRequested $true
    if ($explicitVersion.Version -ne "2021.3.46f1") {
        throw "Runner self-test failed: explicit ProductVersion was not authoritative."
    }

    $automaticFallback = Select-UnityEditorVersion `
        -EditorPath "C:\Program Files\Unity\Hub\Editor\2021.3.45f1c1\Editor\Unity.exe" `
        -ProductVersion "unparseable" `
        -DirectoryVersion $fallbackVersion `
        -ExplicitlyRequested $false
    if ($automaticFallback.Version -ne "2021.3.45f1c1") {
        throw "Runner self-test failed: automatic Hub directory fallback was not used."
    }

    $rejectedExplicitFallback = $false
    try {
        Select-UnityEditorVersion `
            -EditorPath "X:\2021.3.45f1c1\Unity.exe" `
            -ProductVersion "unparseable" `
            -DirectoryVersion $fallbackVersion `
            -ExplicitlyRequested $true
    }
    catch {
        $rejectedExplicitFallback = $_.Exception.Message -like '*must expose a parseable ProductVersion*'
    }

    if (-not $rejectedExplicitFallback) {
        throw "Runner self-test failed: explicit editor ProductVersion rejection contract was not enforced."
    }

    $selfTestId = [guid]::NewGuid().ToString("N")
    $resultPath = Join-Path ([System.IO.Path]::GetTempPath()) "blaze-radar-$selfTestId-results.xml"
    $logPath = Join-Path ([System.IO.Path]::GetTempPath()) "blaze-radar-$selfTestId.log"
    try {
        Set-Content -LiteralPath $resultPath -Encoding UTF8 -Value '<test-run result="Passed" total="3" passed="2" failed="0" inconclusive="0" skipped="1" />'
        Set-Content -LiteralPath $logPath -Encoding UTF8 -Value 'Runner self-test log.'
        Assert-UnityTestResults -ResultPath $resultPath -LogPath $logPath -Platform "SelfTest" -ExitCode 0

        Set-Content -LiteralPath $resultPath -Encoding UTF8 -Value '<test-run result="Passed" total="3" passed="1" failed="0" inconclusive="0" skipped="1" />'
        $rejectedInconsistentCounts = $false
        try {
            Assert-UnityTestResults -ResultPath $resultPath -LogPath $logPath -Platform "SelfTest" -ExitCode 0
        }
        catch {
            $rejectedInconsistentCounts = $_.Exception.Message -like '*inconsistent*'
        }

        if (-not $rejectedInconsistentCounts) {
            throw "Runner self-test failed: inconsistent XML counts were not rejected."
        }
    }
    finally {
        Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
    }

    $sampleTestRoot = Join-Path ([System.IO.Path]::GetTempPath()) "blaze-radar-$selfTestId-samples"
    $basicAssembly = Join-Path $sampleTestRoot "Basic Interaction\Blaze.Interaction.Sample.BasicInteraction.asmdef"
    $multiScreenAssembly = Join-Path $sampleTestRoot "Multi-Surface Routing\Blaze.Interaction.Sample.MultiSurfaceRouting.asmdef"
    try {
        New-Item -ItemType Directory -Path (Split-Path -Parent $basicAssembly), (Split-Path -Parent $multiScreenAssembly) -Force | Out-Null
        Set-Content -LiteralPath $basicAssembly -Encoding UTF8 -Value '{}'
        Set-Content -LiteralPath $multiScreenAssembly -Encoding UTF8 -Value '{}'
        Assert-SampleAssembliesCopied -SampleRoot $sampleTestRoot

        Remove-Item -LiteralPath $multiScreenAssembly -Force
        $rejectedMissingSampleAssembly = $false
        try {
            Assert-SampleAssembliesCopied -SampleRoot $sampleTestRoot
        }
        catch {
            $rejectedMissingSampleAssembly = $_.Exception.Message -like '*was not copied*'
        }

        if (-not $rejectedMissingSampleAssembly) {
            throw "Runner self-test failed: a missing included sample assembly was not rejected."
        }
    }
    finally {
        Remove-Item -LiteralPath $sampleTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Unity package runner self-test PASS: $actual"
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
    if ($null -eq $root -or $root.Name -ne "test-run") {
        $actualRoot = if ($null -eq $root) { "<missing>" } else { $root.Name }
        throw "Unity $Platform test result XML root must be 'test-run', found '$actualRoot': '$ResultPath'."
    }

    if (-not $root.HasAttribute("result") -or $root.GetAttribute("result") -ne "Passed") {
        throw "Unity $Platform test result must report result='Passed': '$ResultPath'."
    }

    $counts = @{}
    foreach ($attributeName in @("total", "passed", "failed", "inconclusive", "skipped")) {
        if (-not $root.HasAttribute($attributeName)) {
            throw "Unity $Platform test result is missing required '$attributeName' count: '$ResultPath'."
        }

        $parsedCount = 0
        if (-not [int]::TryParse($root.GetAttribute($attributeName), [ref]$parsedCount) -or $parsedCount -lt 0) {
            throw "Unity $Platform test result has invalid non-negative '$attributeName' count: '$ResultPath'."
        }

        $counts[$attributeName] = $parsedCount
    }

    $total = $counts["total"]
    $passed = $counts["passed"]
    $failed = $counts["failed"]
    $inconclusive = $counts["inconclusive"]
    $skipped = $counts["skipped"]
    if ($total -le 0) {
        throw "Unity $Platform discovered no tests. Result: '$ResultPath'. Log: '$LogPath'."
    }

    if ($total -ne ($passed + $failed + $inconclusive + $skipped)) {
        throw "Unity $Platform test result counts are inconsistent: total=$total, passed=$passed, failed=$failed, inconclusive=$inconclusive, skipped=$skipped. Result: '$ResultPath'."
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

if ($RunnerSelfTest) {
    Invoke-RunnerSelfTest
    return
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$packageRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "UnityPackage\com.blaze.interaction"))
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
    "com.blaze.interaction": "file:../../../UnityPackage/com.blaze.interaction",
    "com.unity.test-framework": "1.1.33",
    "com.unity.ugui": "1.0.0",
    "com.unity.nuget.newtonsoft-json": "3.0.2"
  },
  "testables": ["com.blaze.interaction"]
}
'@
Set-Content -LiteralPath (Join-Path $packagesDirectory "manifest.json") -Value $manifest -Encoding UTF8
Set-Content -LiteralPath (Join-Path $projectSettingsDirectory "ProjectVersion.txt") -Value "m_EditorVersion: $($editor.Version)" -Encoding UTF8

if ($IncludeSamples) {
    $packageJson = Get-Content -LiteralPath (Join-Path $packageRoot "package.json") -Raw | ConvertFrom-Json
    $sampleRoot = Join-Path $assetsDirectory ("Samples\Blaze Interaction SDK\" + $packageJson.version)
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

    Assert-SampleAssembliesCopied -SampleRoot $sampleRoot
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
        "-logFile", $logPath
    )

    $argumentLine = ($arguments | ForEach-Object {
        '"' + $_.Replace('"', '\"') + '"'
    }) -join ' '
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $editor.Path
    $startInfo.Arguments = $argumentLine
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Unity $platform process could not be started."
    }
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $process.Dispose()
    Assert-UnityTestResults `
        -ResultPath $resultPath `
        -LogPath $logPath `
        -Platform $platform `
        -ExitCode $exitCode
}

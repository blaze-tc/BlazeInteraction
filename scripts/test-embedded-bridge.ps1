[CmdletBinding()]
param(
    [ValidateRange(1, 60)]
    [int]$StartupTimeoutSeconds = 15,
    [ValidateRange(0, 30)]
    [int]$SetupDelaySeconds = 0,
    [switch]$InjectSetupFailure,
    [string]$DiagnosticProcessFile = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-ExactSmokeDirectory {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] [string]$ExpectedPath)
    $actual = [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $expected = [System.IO.Path]::GetFullPath($ExpectedPath).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if (-not [System.IO.Path]::IsPathRooted($Path) -or
        -not [string]::Equals($actual, $expected, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Smoke-test cleanup target is not the exact approved directory. Expected '$expected', got '$actual'."
    }
}

function Assert-SmokeChildDirectory {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] [string]$ParentPath)
    $actual = [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $parent = [System.IO.Path]::GetFullPath($ParentPath).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $parentPrefix = $parent + [System.IO.Path]::DirectorySeparatorChar
    if (-not [System.IO.Path]::IsPathRooted($Path) -or
        [string]::Equals($actual, $parent, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $actual.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Smoke-test child cleanup target must be beneath '$parent'; got '$actual'."
    }
}

function Stop-OwnedProcess {
    param([System.Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            [void]$Process.WaitForExit(5000)
        }
    }
    catch { }
}

function Invoke-ProviderSmoke {
    param(
        [Parameter(Mandatory)] [string]$ExpectedProviderId,
        [Parameter(Mandatory)] [string]$ExpectedProviderInstanceId,
        [Parameter(Mandatory)] [string]$ExpectedWindowTitlePrefix,
        [string]$SelectedProviderId = '',
        [Parameter(Mandatory)] [string]$SuccessMessage,
        [switch]$InjectFailure
    )

    if (Test-Path -LiteralPath $clientResult) {
        Remove-Item -LiteralPath $clientResult -Force
    }
    $pipeName = "Blaze.InteractionBridge.ReleaseSmoke.$([Guid]::NewGuid().ToString('N'))"

    # The helper is both the advertised Unity parent and the actual Pipe client. This exercises
    # the production PID-authentication boundary instead of bypassing it from the outer harness.
    $clientScript = Join-Path $PSScriptRoot 'embedded-bridge-smoke-client.ps1'
    $clientStartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $clientStartInfo.FileName = 'powershell.exe'
    $clientStartInfo.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$clientScript`" -PipeName `"$pipeName`" -ResultFile `"$clientResult`" -ExpectedProviderId `"$ExpectedProviderId`" -ExpectedProviderInstanceId `"$ExpectedProviderInstanceId`" -StartupTimeoutSeconds $StartupTimeoutSeconds -SetupDelaySeconds $SetupDelaySeconds"
    $clientStartInfo.WorkingDirectory = $repositoryRoot
    $clientStartInfo.UseShellExecute = $false
    $clientStartInfo.CreateNoWindow = $true
    $script:parentProcess = [System.Diagnostics.Process]::Start($clientStartInfo)

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $executable
    $startInfo.Arguments = "--providers-root `"$providersRoot`" --data-root `"$dataRoot`" --pipe-name `"$pipeName`" --parent-pid $($script:parentProcess.Id) --minimized"
    if (-not [string]::IsNullOrWhiteSpace($SelectedProviderId)) {
        $startInfo.Arguments += " --provider `"$SelectedProviderId`""
    }
    $startInfo.WorkingDirectory = $embeddedDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $script:bridgeProcess = [System.Diagnostics.Process]::Start($startInfo)

    if ($InjectFailure) {
        if (-not [string]::IsNullOrWhiteSpace($DiagnosticProcessFile)) {
            [System.IO.File]::WriteAllLines(
                [System.IO.Path]::GetFullPath($DiagnosticProcessFile),
                @("parent=$($script:parentProcess.Id)", "bridge=$($script:bridgeProcess.Id)"),
                [System.Text.UTF8Encoding]::new($false))
        }
        throw 'Injected setup failure after owned processes started.'
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $windowTitle = ''
    $windowHandle = [IntPtr]::Zero
    $clientCompleted = $false
    while ([DateTime]::UtcNow -lt $deadline -and -not $script:bridgeProcess.HasExited) {
        Start-Sleep -Milliseconds 200
        $script:bridgeProcess.Refresh()
        $script:parentProcess.Refresh()
        $windowTitle = $script:bridgeProcess.MainWindowTitle
        $windowHandle = $script:bridgeProcess.MainWindowHandle
        $clientCompleted = Test-Path -LiteralPath $clientResult -PathType Leaf
        if ($script:parentProcess.HasExited -and -not $clientCompleted) {
            throw "Embedded smoke client exited before reporting a result with code $($script:parentProcess.ExitCode)."
        }
        if ($windowHandle -ne [IntPtr]::Zero -and
            $windowTitle.StartsWith($ExpectedWindowTitlePrefix, [System.StringComparison]::Ordinal) -and
            $clientCompleted) { break }
    }
    if ($script:bridgeProcess.HasExited) { throw "Embedded BlazeInteractionBridge exited during startup with code $($script:bridgeProcess.ExitCode)." }
    if ($windowHandle -eq [IntPtr]::Zero -or [string]::IsNullOrWhiteSpace($windowTitle)) { throw 'Embedded BlazeInteractionBridge did not create a top-level window.' }
    if (-not $windowTitle.StartsWith($ExpectedWindowTitlePrefix, [System.StringComparison]::Ordinal)) {
        throw "Embedded BlazeInteractionBridge window title mismatch. Expected prefix '$ExpectedWindowTitlePrefix', got '$windowTitle'."
    }
    if ($windowTitle -match '失败|错误|failed|error') { throw "Embedded BlazeInteractionBridge displayed an error window: $windowTitle" }
    if (-not $clientCompleted) { throw 'Embedded smoke client did not complete the IPC frame check before timeout.' }

    $clientResultText = (Get-Content -LiteralPath $clientResult -Raw -Encoding UTF8).Trim()
    if ($clientResultText -ne 'OK') { throw "Embedded smoke client failed: $clientResultText" }

    Stop-OwnedProcess -Process $script:parentProcess
    if (-not $script:bridgeProcess.WaitForExit(10000)) { throw 'Embedded BlazeInteractionBridge did not exit after its parent process ended.' }
    if ($script:bridgeProcess.ExitCode -ne 0) { throw "Embedded BlazeInteractionBridge exited with code $($script:bridgeProcess.ExitCode)." }

    Write-Host "Embedded BlazeInteractionBridge top-level window passed: $windowTitle"
    Write-Host $SuccessMessage
    Write-Host 'Parent-process shutdown passed with exit code 0.'
    $script:parentProcess.Dispose()
    $script:bridgeProcess.Dispose()
    $script:parentProcess = $null
    $script:bridgeProcess = $null
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$embeddedDirectory = Join-Path $repositoryRoot 'UnityPackage\com.blaze.interaction\Bridge~\win-x64'
$executable = Join-Path $embeddedDirectory 'BlazeInteractionBridge.exe'
$versionMarker = Join-Path $embeddedDirectory 'bridge-version.txt'
$smokeDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'tmp\embedded-bridge-smoke'))
$expectedSmokeDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'tmp\embedded-bridge-smoke'))
$providersRoot = Join-Path $smokeDirectory 'Providers'
$radarProviderDirectory = Join-Path $providersRoot 'Radar'
$cameraProviderDirectory = Join-Path $providersRoot 'CameraVision'
$profile = Join-Path $radarProviderDirectory 'profiles\radar-default.json'
$dataRoot = [System.IO.Path]::GetFullPath((Join-Path $smokeDirectory ("Data-" + [Guid]::NewGuid().ToString('N'))))
$clientResult = Join-Path $smokeDirectory 'client-result.txt'
$parentProcess = $null
$bridgeProcess = $null

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Embedded BlazeInteractionBridge executable was not found: $executable" }
if (-not (Test-Path -LiteralPath $versionMarker -PathType Leaf)) { throw "Embedded BlazeInteractionBridge version marker was not found: $versionMarker" }
$embeddedVersion = (Get-Content -LiteralPath $versionMarker -Raw -Encoding UTF8).Trim()
if ($embeddedVersion -ne '1.0.0') { throw "Embedded BlazeInteractionBridge version marker must be 1.0.0; found '$embeddedVersion'." }
if ($SetupDelaySeconds -ge $StartupTimeoutSeconds) { throw 'SetupDelaySeconds must be less than StartupTimeoutSeconds.' }

try {
    # All temporary mutation is guarded so even parsing or injected setup failures clean up.
    Assert-ExactSmokeDirectory -Path $smokeDirectory -ExpectedPath $expectedSmokeDirectory
    if (Test-Path -LiteralPath $smokeDirectory) {
        Remove-Item -LiteralPath $smokeDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $smokeDirectory | Out-Null
    Assert-SmokeChildDirectory -Path $dataRoot -ParentPath $smokeDirectory
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null

    New-Item -ItemType Directory -Force -Path $providersRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path $embeddedDirectory 'Providers\Radar') `
        -Destination $radarProviderDirectory -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $embeddedDirectory 'Providers\CameraVision') `
        -Destination $cameraProviderDirectory -Recurse -Force

    $profileJson = Get-Content -LiteralPath (Join-Path $repositoryRoot 'config\default-profile.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $profileJson.screens[0].sensors[0].sourceMode = 'simulation'
    $profileJson.screens[0].sensors[0].range.activePolygon = @(
        [ordered]@{ x = -5; y = -5 },
        [ordered]@{ x = 5; y = -5 },
        [ordered]@{ x = 5; y = 5 },
        [ordered]@{ x = -5; y = 5 }
    )
    [System.IO.File]::WriteAllText(
        $profile,
        ($profileJson | ConvertTo-Json -Depth 32),
        [System.Text.UTF8Encoding]::new($false))

    Invoke-ProviderSmoke `
        -ExpectedProviderId 'blaze.radar.f10f20' `
        -ExpectedProviderInstanceId 'radar-main' `
        -ExpectedWindowTitlePrefix 'RadarBridge' `
        -SuccessMessage 'Radar standard InteractionFrame passed.' `
        -InjectFailure:$InjectSetupFailure
    Write-Host 'Interaction IPC 1 Hello/HelloAck passed with Bridge version 1.0.0.'
    Invoke-ProviderSmoke `
        -ExpectedProviderId 'blaze.camera.vision' `
        -ExpectedProviderInstanceId 'camera-vision-main' `
        -ExpectedWindowTitlePrefix 'Blaze Interaction Bridge' `
        -SelectedProviderId 'blaze.camera.vision' `
        -SuccessMessage 'CameraVision fake standard InteractionFrame passed.'
}
finally {
    Stop-OwnedProcess -Process $bridgeProcess
    Stop-OwnedProcess -Process $parentProcess
    Assert-SmokeChildDirectory -Path $dataRoot -ParentPath $smokeDirectory
    if (Test-Path -LiteralPath $dataRoot) {
        Remove-Item -LiteralPath $dataRoot -Recurse -Force
    }
    Assert-ExactSmokeDirectory -Path $smokeDirectory -ExpectedPath $expectedSmokeDirectory
    if (Test-Path -LiteralPath $smokeDirectory) {
        Remove-Item -LiteralPath $smokeDirectory -Recurse -Force
    }
}

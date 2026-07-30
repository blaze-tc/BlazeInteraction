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

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$embeddedDirectory = Join-Path $repositoryRoot 'UnityPackage\com.blaze.radar\Bridge~\win-x64'
$executable = Join-Path $embeddedDirectory 'RadarBridge.exe'
$versionMarker = Join-Path $embeddedDirectory 'bridge-version.txt'
$smokeDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'tmp\embedded-bridge-smoke'))
$expectedSmokeDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'tmp\embedded-bridge-smoke'))
$profile = Join-Path $smokeDirectory 'profile.json'
$clientResult = Join-Path $smokeDirectory 'client-result.txt'
$parentProcess = $null
$bridgeProcess = $null

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Embedded RadarBridge executable was not found: $executable" }
if (-not (Test-Path -LiteralPath $versionMarker -PathType Leaf)) { throw "Embedded RadarBridge version marker was not found: $versionMarker" }
$embeddedVersion = (Get-Content -LiteralPath $versionMarker -Raw -Encoding UTF8).Trim()
if ($embeddedVersion -ne '1.2.2') { throw "Embedded RadarBridge version marker must be 1.2.2; found '$embeddedVersion'." }
if ($SetupDelaySeconds -ge $StartupTimeoutSeconds) { throw 'SetupDelaySeconds must be less than StartupTimeoutSeconds.' }

try {
    # All temporary mutation is guarded so even parsing or injected setup failures clean up.
    Assert-ExactSmokeDirectory -Path $smokeDirectory -ExpectedPath $expectedSmokeDirectory
    if (Test-Path -LiteralPath $smokeDirectory) {
        Remove-Item -LiteralPath $smokeDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $smokeDirectory | Out-Null

    $pipeName = "RadarControl.ReleaseSmoke.$([Guid]::NewGuid().ToString('N'))"
    $profileJson = Get-Content -LiteralPath (Join-Path $repositoryRoot 'config\default-profile.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $profileJson.ipc.pipeName = $pipeName
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

    # The helper is both the advertised Unity parent and the actual Pipe client. This exercises
    # the production PID-authentication boundary instead of bypassing it from the outer harness.
    $clientScript = Join-Path $PSScriptRoot 'embedded-bridge-smoke-client.ps1'
    $clientStartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $clientStartInfo.FileName = 'powershell.exe'
    $clientStartInfo.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$clientScript`" -PipeName `"$pipeName`" -ResultFile `"$clientResult`" -StartupTimeoutSeconds $StartupTimeoutSeconds -SetupDelaySeconds $SetupDelaySeconds"
    $clientStartInfo.WorkingDirectory = $repositoryRoot
    $clientStartInfo.UseShellExecute = $false
    $clientStartInfo.CreateNoWindow = $true
    $parentProcess = [System.Diagnostics.Process]::Start($clientStartInfo)
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $executable
    $startInfo.Arguments = "--profile `"$profile`" --parent-pid $($parentProcess.Id) --minimized"
    $startInfo.WorkingDirectory = $embeddedDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $bridgeProcess = [System.Diagnostics.Process]::Start($startInfo)

    if (-not [string]::IsNullOrWhiteSpace($DiagnosticProcessFile)) {
        [System.IO.File]::WriteAllLines(
            [System.IO.Path]::GetFullPath($DiagnosticProcessFile),
            @("parent=$($parentProcess.Id)", "bridge=$($bridgeProcess.Id)"),
            [System.Text.UTF8Encoding]::new($false))
    }
    if ($InjectSetupFailure) { throw 'Injected setup failure after owned processes started.' }

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $windowTitle = ''
    $windowHandle = [IntPtr]::Zero
    $clientCompleted = $false
    while ([DateTime]::UtcNow -lt $deadline -and -not $bridgeProcess.HasExited) {
        Start-Sleep -Milliseconds 200
        $bridgeProcess.Refresh()
        $parentProcess.Refresh()
        $windowTitle = $bridgeProcess.MainWindowTitle
        $windowHandle = $bridgeProcess.MainWindowHandle
        $clientCompleted = Test-Path -LiteralPath $clientResult -PathType Leaf
        if ($parentProcess.HasExited -and -not $clientCompleted) {
            throw "Embedded smoke client exited before reporting a result with code $($parentProcess.ExitCode)."
        }
        if ($windowHandle -ne [IntPtr]::Zero -and
            -not [string]::IsNullOrWhiteSpace($windowTitle) -and
            $clientCompleted) { break }
    }
    if ($bridgeProcess.HasExited) { throw "Embedded RadarBridge exited during startup with code $($bridgeProcess.ExitCode)." }
    if ($windowHandle -eq [IntPtr]::Zero -or [string]::IsNullOrWhiteSpace($windowTitle)) { throw 'Embedded RadarBridge did not create a top-level window.' }
    if ($windowTitle -match '失败|错误|failed|error') { throw "Embedded RadarBridge displayed an error window: $windowTitle" }

    if (-not $clientCompleted) { throw 'Embedded smoke client did not complete the IPC handshake before timeout.' }
    $clientResultText = (Get-Content -LiteralPath $clientResult -Raw -Encoding UTF8).Trim()
    if ($clientResultText -ne 'OK') { throw "Embedded smoke client failed: $clientResultText" }

    Stop-OwnedProcess -Process $parentProcess
    if (-not $bridgeProcess.WaitForExit(10000)) { throw 'Embedded RadarBridge did not exit after its parent process ended.' }
    if ($bridgeProcess.ExitCode -ne 0) { throw "Embedded RadarBridge exited with code $($bridgeProcess.ExitCode)." }

    Write-Host "Embedded RadarBridge top-level window passed: $windowTitle"
    Write-Host 'IPC v2 Hello/HelloAck passed with Bridge version 1.2.2.'
    Write-Host 'Parent-process shutdown passed with exit code 0.'
}
finally {
    Stop-OwnedProcess -Process $bridgeProcess
    Stop-OwnedProcess -Process $parentProcess
    Assert-ExactSmokeDirectory -Path $smokeDirectory -ExpectedPath $expectedSmokeDirectory
    if (Test-Path -LiteralPath $smokeDirectory) {
        Remove-Item -LiteralPath $smokeDirectory -Recurse -Force
    }
}

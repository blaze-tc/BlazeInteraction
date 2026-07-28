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

function Read-ExactBytes {
    param(
        [Parameter(Mandatory)] [System.IO.Stream]$Stream,
        [Parameter(Mandatory)] [int]$Count,
        [Parameter(Mandatory)] [int]$TimeoutMilliseconds
    )
    $buffer = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $task = $Stream.ReadAsync($buffer, $offset, $Count - $offset)
        if (-not $task.Wait($TimeoutMilliseconds)) { throw "Timed out reading $Count bytes from the Bridge IPC pipe." }
        $read = $task.Result
        if ($read -eq 0) { throw 'Bridge IPC pipe closed before the response was complete.' }
        $offset += $read
    }
    return $buffer
}

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
$parentProcess = $null
$bridgeProcess = $null
$pipeClient = $null

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Embedded RadarBridge executable was not found: $executable" }
if (-not (Test-Path -LiteralPath $versionMarker -PathType Leaf)) { throw "Embedded RadarBridge version marker was not found: $versionMarker" }
$embeddedVersion = (Get-Content -LiteralPath $versionMarker -Raw -Encoding UTF8).Trim()
if ($embeddedVersion -ne '1.2.0') { throw "Embedded RadarBridge version marker must be 1.2.0; found '$embeddedVersion'." }
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

    # Hold the helper alive beyond the advertised timeout. It is terminated only after window/IPC assertions.
    $helperLifetimeSeconds = $StartupTimeoutSeconds + 30
    $parentProcess = Start-Process -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', "Start-Sleep -Seconds $helperLifetimeSeconds") `
        -WindowStyle Hidden -PassThru
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
    if ($SetupDelaySeconds -gt 0) { Start-Sleep -Seconds $SetupDelaySeconds }
    $windowTitle = ''
    $windowHandle = [IntPtr]::Zero
    while ([DateTime]::UtcNow -lt $deadline -and -not $bridgeProcess.HasExited) {
        Start-Sleep -Milliseconds 200
        $bridgeProcess.Refresh()
        $windowTitle = $bridgeProcess.MainWindowTitle
        $windowHandle = $bridgeProcess.MainWindowHandle
        if ($windowHandle -ne [IntPtr]::Zero -and -not [string]::IsNullOrWhiteSpace($windowTitle)) { break }
    }
    if ($bridgeProcess.HasExited) { throw "Embedded RadarBridge exited during startup with code $($bridgeProcess.ExitCode)." }
    if ($windowHandle -eq [IntPtr]::Zero -or [string]::IsNullOrWhiteSpace($windowTitle)) { throw 'Embedded RadarBridge did not create a top-level window.' }
    if ($windowTitle -match '失败|错误|failed|error') { throw "Embedded RadarBridge displayed an error window: $windowTitle" }

    $pipeClient = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    $pipeClient.Connect(5000)
    $hello = [ordered]@{
        protocolVersion = 2
        messageType = 'Hello'
        sequence = 1
        timestampUnixMilliseconds = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        payload = [ordered]@{
            unityProcessId = $PID
            unityVersion = 'release-smoke'
            screens = @([ordered]@{
                screenId = 'main'; name = 'Smoke Primary'; defaultWidthPixels = 1920; defaultHeightPixels = 1080
                isPrimary = $true; order = 0
            })
        }
    }
    $helloBytes = [System.Text.Encoding]::UTF8.GetBytes(($hello | ConvertTo-Json -Depth 16 -Compress))
    $lengthPrefix = [System.BitConverter]::GetBytes([int]$helloBytes.Length)
    $pipeClient.Write($lengthPrefix, 0, $lengthPrefix.Length)
    $pipeClient.Write($helloBytes, 0, $helloBytes.Length)
    $pipeClient.Flush()

    $responseLengthBytes = Read-ExactBytes -Stream $pipeClient -Count 4 -TimeoutMilliseconds 5000
    $responseLength = [System.BitConverter]::ToInt32($responseLengthBytes, 0)
    if ($responseLength -lt 1 -or $responseLength -gt (4 * 1024 * 1024)) { throw "Bridge IPC returned invalid payload length $responseLength." }
    $responseBytes = Read-ExactBytes -Stream $pipeClient -Count $responseLength -TimeoutMilliseconds 5000
    $response = [System.Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json
    if ($response.protocolVersion -ne 2 -or $response.messageType -ne 'HelloAck') {
        $responsePayload = $response.payload | ConvertTo-Json -Depth 8 -Compress
        throw "Expected IPC v2 HelloAck; received protocol '$($response.protocolVersion)' message '$($response.messageType)' payload '$responsePayload'."
    }
    if ($response.payload.protocolVersion -ne 2 -or $response.payload.bridgeVersion -ne '1.2.0') {
        throw "HelloAck identity mismatch: Bridge '$($response.payload.bridgeVersion)', IPC '$($response.payload.protocolVersion)'."
    }

    $pipeClient.Dispose()
    $pipeClient = $null
    Stop-OwnedProcess -Process $parentProcess
    if (-not $bridgeProcess.WaitForExit(10000)) { throw 'Embedded RadarBridge did not exit after its parent process ended.' }
    if ($bridgeProcess.ExitCode -ne 0) { throw "Embedded RadarBridge exited with code $($bridgeProcess.ExitCode)." }

    Write-Host "Embedded RadarBridge top-level window passed: $windowTitle"
    Write-Host 'IPC v2 Hello/HelloAck passed with Bridge version 1.2.0.'
    Write-Host 'Parent-process shutdown passed with exit code 0.'
}
finally {
    if ($pipeClient) { try { $pipeClient.Dispose() } catch { } }
    Stop-OwnedProcess -Process $bridgeProcess
    Stop-OwnedProcess -Process $parentProcess
    Assert-ExactSmokeDirectory -Path $smokeDirectory -ExpectedPath $expectedSmokeDirectory
    if (Test-Path -LiteralPath $smokeDirectory) {
        Remove-Item -LiteralPath $smokeDirectory -Recurse -Force
    }
}

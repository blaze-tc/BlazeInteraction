[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PipeName,
    [Parameter(Mandatory)]
    [string]$ResultFile,
    [Parameter(Mandatory)]
    [string]$ExpectedProviderId,
    [Parameter(Mandatory)]
    [string]$ExpectedProviderInstanceId,
    [ValidateRange(1, 60)]
    [int]$StartupTimeoutSeconds = 15,
    [ValidateRange(0, 30)]
    [int]$SetupDelaySeconds = 0
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
        if (-not $task.Wait($TimeoutMilliseconds)) {
            throw "Timed out reading $Count bytes from the Bridge IPC pipe."
        }

        $read = $task.Result
        if ($read -eq 0) { throw 'Bridge IPC pipe closed before the response was complete.' }
        $offset += $read
    }

    return $buffer
}

function Write-SmokeResult {
    param([Parameter(Mandatory)] [string]$Value)

    $temporaryResult = "$ResultFile.$PID.tmp"
    [System.IO.File]::WriteAllText(
        $temporaryResult,
        $Value,
        [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryResult -Destination $ResultFile -Force
}

function Read-IpcEnvelope {
    param(
        [Parameter(Mandatory)] [System.IO.Stream]$Stream,
        [Parameter(Mandatory)] [int]$TimeoutMilliseconds
    )

    $lengthBytes = Read-ExactBytes -Stream $Stream -Count 4 -TimeoutMilliseconds $TimeoutMilliseconds
    $length = [System.BitConverter]::ToInt32($lengthBytes, 0)
    if ($length -lt 1 -or $length -gt (4 * 1024 * 1024)) {
        throw "Bridge IPC returned invalid payload length $length."
    }

    $payloadBytes = Read-ExactBytes -Stream $Stream -Count $length -TimeoutMilliseconds $TimeoutMilliseconds
    return [System.Text.Encoding]::UTF8.GetString($payloadBytes) | ConvertFrom-Json
}

$pipeClient = $null
try {
    if ($SetupDelaySeconds -gt 0) { Start-Sleep -Seconds $SetupDelaySeconds }

    $pipeClient = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    $pipeClient.Connect($StartupTimeoutSeconds * 1000)

    $hello = [ordered]@{
        protocolVersion = 1
        messageType = 'Hello'
        sequence = 1
        payload = [ordered]@{
            unityPid = $PID
            unityVersion = 'release-smoke'
            sdkVersion = '1.0.0'
            surfaces = @([ordered]@{
                surfaceId = 'main'; name = 'Smoke Primary'; logicalWidth = 1920; logicalHeight = 1080
                isPrimary = $true; order = 0
            })
        }
    }
    $helloBytes = [System.Text.Encoding]::UTF8.GetBytes(($hello | ConvertTo-Json -Depth 16 -Compress))
    $lengthPrefix = [System.BitConverter]::GetBytes([int]$helloBytes.Length)
    $pipeClient.Write($lengthPrefix, 0, $lengthPrefix.Length)
    $pipeClient.Write($helloBytes, 0, $helloBytes.Length)
    $pipeClient.Flush()

    $response = Read-IpcEnvelope -Stream $pipeClient -TimeoutMilliseconds 5000
    if ($response.protocolVersion -ne 1 -or $response.messageType -ne 'HelloAck') {
        $responsePayload = $response.payload | ConvertTo-Json -Depth 8 -Compress
        throw "Expected Interaction IPC 1 HelloAck; received protocol '$($response.protocolVersion)' message '$($response.messageType)' payload '$responsePayload'."
    }
    if ($response.payload.bridgeVersion -ne '1.0.0') {
        throw "HelloAck identity mismatch: Bridge '$($response.payload.bridgeVersion)', IPC '$($response.protocolVersion)'."
    }
    if ($response.payload.activeProvider.id -cne $ExpectedProviderId -or
        $response.payload.activeProvider.instanceId -cne $ExpectedProviderInstanceId) {
        throw "HelloAck provider mismatch: expected '$ExpectedProviderId/$ExpectedProviderInstanceId', got '$($response.payload.activeProvider.id)/$($response.payload.activeProvider.instanceId)'."
    }

    $frame = $null
    do {
        $message = Read-IpcEnvelope -Stream $pipeClient -TimeoutMilliseconds 5000
        if ($message.protocolVersion -ne 1) {
            throw "Expected Interaction IPC 1; received protocol '$($message.protocolVersion)'."
        }
        if ($message.messageType -eq 'InteractionFrame') { $frame = $message }
    } while ($null -eq $frame)

    if ($frame.payload.providerId -cne $ExpectedProviderId -or
        $frame.payload.providerInstanceId -cne $ExpectedProviderInstanceId -or
        $frame.payload.surfaceId -cne 'main') {
        throw "InteractionFrame identity mismatch for '$ExpectedProviderId/$ExpectedProviderInstanceId'."
    }

    Write-SmokeResult -Value 'OK'

    # Remain the verified Unity/parent process until the outer smoke harness deliberately ends us.
    Start-Sleep -Seconds ($StartupTimeoutSeconds + 30)
}
catch {
    Write-SmokeResult -Value "ERROR: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($pipeClient) { try { $pipeClient.Dispose() } catch { } }
}

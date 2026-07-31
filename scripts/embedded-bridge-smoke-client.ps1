[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PipeName,
    [Parameter(Mandatory)]
    [string]$ResultFile,
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

$pipeClient = $null
try {
    if ($SetupDelaySeconds -gt 0) { Start-Sleep -Seconds $SetupDelaySeconds }

    $pipeClient = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    $pipeClient.Connect($StartupTimeoutSeconds * 1000)

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
    if ($responseLength -lt 1 -or $responseLength -gt (4 * 1024 * 1024)) {
        throw "Bridge IPC returned invalid payload length $responseLength."
    }

    $responseBytes = Read-ExactBytes -Stream $pipeClient -Count $responseLength -TimeoutMilliseconds 5000
    $response = [System.Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json
    if ($response.protocolVersion -ne 2 -or $response.messageType -ne 'HelloAck') {
        $responsePayload = $response.payload | ConvertTo-Json -Depth 8 -Compress
        throw "Expected IPC v2 HelloAck; received protocol '$($response.protocolVersion)' message '$($response.messageType)' payload '$responsePayload'."
    }
    if ($response.payload.protocolVersion -ne 2 -or $response.payload.bridgeVersion -ne '1.2.8') {
        throw "HelloAck identity mismatch: Bridge '$($response.payload.bridgeVersion)', IPC '$($response.payload.protocolVersion)'."
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

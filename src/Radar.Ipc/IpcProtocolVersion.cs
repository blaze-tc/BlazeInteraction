using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Ipc;

public sealed record IpcVersionValidationResult(bool IsCompatible, string? Error);

public static class IpcProtocolVersion
{
    public const int Current = 2;

    public static IpcVersionValidationResult Validate(IpcEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.ProtocolVersion == Current
            ? new IpcVersionValidationResult(true, null)
            : new IpcVersionValidationResult(
                false,
                $"IPC protocol version {envelope.ProtocolVersion} is incompatible with Bridge protocol version {Current}.");
    }
}

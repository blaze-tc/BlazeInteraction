namespace Blaze.Interaction.Ipc;

public static class InteractionIpcProtocol
{
    public const int CurrentVersion = 1;
    public const string PipeName = "Blaze.InteractionBridge";
    public const int LengthPrefixSize = sizeof(int);
    public const int DefaultMaximumPayloadLength = 4 * 1024 * 1024;
}

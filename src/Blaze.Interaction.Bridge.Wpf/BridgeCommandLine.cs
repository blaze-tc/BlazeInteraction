using System.IO;
using Blaze.Interaction.Ipc;

namespace Blaze.Interaction.Bridge.Wpf;

public sealed record BridgeLaunchOptions(
    int? ParentProcessId,
    bool Minimized,
    string? ProvidersRoot,
    string? ProviderId,
    string PipeName);

public static class BridgeCommandLine
{
    public static BridgeLaunchOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        int? parentProcessId = null;
        var minimized = false;
        string? providersRoot = null;
        string? providerId = null;
        var pipeName = InteractionIpcProtocol.PipeName;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase))
            {
                minimized = true;
                continue;
            }

            var value = ReadValue(arguments, ref index, argument);
            if (string.Equals(argument, "--parent-pid", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, out var parsed) || parsed <= 0)
                {
                    throw new ArgumentException("--parent-pid requires a positive process ID.", nameof(arguments));
                }

                parentProcessId = parsed;
            }
            else if (string.Equals(argument, "--providers-root", StringComparison.OrdinalIgnoreCase))
            {
                providersRoot = Path.GetFullPath(value);
            }
            else if (string.Equals(argument, "--provider", StringComparison.OrdinalIgnoreCase))
            {
                providerId = RequireText(value, argument);
            }
            else if (string.Equals(argument, "--pipe-name", StringComparison.OrdinalIgnoreCase))
            {
                pipeName = RequireText(value, argument);
            }
            else
            {
                throw new ArgumentException($"Unknown bridge argument: {argument}", nameof(arguments));
            }
        }

        return new BridgeLaunchOptions(parentProcessId, minimized, providersRoot, providerId, pipeName);
    }

    private static string ReadValue(IReadOnlyList<string> arguments, ref int index, string argument)
    {
        if (index + 1 >= arguments.Count)
        {
            throw new ArgumentException($"{argument} requires a value.", nameof(arguments));
        }

        index++;
        return RequireText(arguments[index], argument);
    }

    private static string RequireText(string? value, string argument)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{argument} requires a non-empty value.", nameof(value))
            : value;
    }
}

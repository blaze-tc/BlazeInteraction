using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blaze.Interaction.Contracts;

public sealed record ProviderIdentity
{
    public required string ProviderId { get; init; }

    public required string ProviderInstanceId { get; init; }
}

public sealed record InteractionSurface
{
    public required string SurfaceId { get; init; }

    public required string Name { get; init; }

    public required int LogicalWidth { get; init; }

    public required int LogicalHeight { get; init; }

    public required bool IsPrimary { get; init; }

    public required int Order { get; init; }
}

public sealed record Vector2Data(float X, float Y);

public enum InteractionPhase
{
    Hover = 0,
    Down = 1,
    Move = 2,
    Up = 3,
    Cancel = 4
}

public enum InteractionHandedness
{
    Unknown = 0,
    Left = 1,
    Right = 2
}

public sealed record InteractionPoint
{
    public required long Id { get; init; }

    public required string SurfaceId { get; init; }

    public required string ProviderId { get; init; }

    public required string ProviderInstanceId { get; init; }

    public required string SourceId { get; init; }

    public required InteractionPhase Phase { get; init; }

    public required Vector2Data NormalizedPosition { get; init; }

    public required Vector2Data PixelPosition { get; init; }

    public required float Confidence { get; init; }

    public required long TimestampUnixMs { get; init; }

    public InteractionExtensions? Extensions { get; init; }
}

public sealed record InteractionFrame
{
    public required string ProviderId { get; init; }

    public required string ProviderInstanceId { get; init; }

    public required string SurfaceId { get; init; }

    public required long Sequence { get; init; }

    public required long TimestampUnixMs { get; init; }

    public required IReadOnlyList<InteractionPoint> Points { get; init; }
}

public static class InteractionJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }

    public static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidDataException($"The {typeof(T).Name} JSON payload is empty or invalid.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

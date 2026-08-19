using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blaze.Interaction.Contracts;

public sealed record ProviderIdentity
{
    private string _providerId = null!;
    private string _providerInstanceId = null!;

    public required string ProviderId
    {
        get => _providerId;
        init => _providerId = InteractionContractGuard.NotBlank(value, nameof(ProviderId));
    }

    public required string ProviderInstanceId
    {
        get => _providerInstanceId;
        init => _providerInstanceId = InteractionContractGuard.NotBlank(value, nameof(ProviderInstanceId));
    }
}

public sealed record InteractionSurface
{
    private string _surfaceId = null!;
    private string _name = null!;
    private int _logicalWidth;
    private int _logicalHeight;

    public required string SurfaceId
    {
        get => _surfaceId;
        init => _surfaceId = InteractionContractGuard.NotBlank(value, nameof(SurfaceId));
    }

    public required string Name
    {
        get => _name;
        init => _name = InteractionContractGuard.NotBlank(value, nameof(Name));
    }

    public required int LogicalWidth
    {
        get => _logicalWidth;
        init => _logicalWidth = InteractionContractGuard.Positive(value, nameof(LogicalWidth));
    }

    public required int LogicalHeight
    {
        get => _logicalHeight;
        init => _logicalHeight = InteractionContractGuard.Positive(value, nameof(LogicalHeight));
    }

    public required bool IsPrimary { get; init; }

    public required int Order { get; init; }
}

[JsonConverter(typeof(Vector2DataJsonConverter))]
public sealed record Vector2Data
{
    [JsonConstructor]
    public Vector2Data(float x, float y)
    {
        X = InteractionContractGuard.Finite(x, nameof(x));
        Y = InteractionContractGuard.Finite(y, nameof(y));
    }

    public float X { get; }

    public float Y { get; }
}

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
    private string _surfaceId = null!;
    private string _providerId = null!;
    private string _providerInstanceId = null!;
    private string _sourceId = null!;
    private InteractionPhase _phase;
    private Vector2Data _normalizedPosition = null!;
    private Vector2Data _pixelPosition = null!;
    private float _confidence;

    public required long Id { get; init; }

    public required string SurfaceId
    {
        get => _surfaceId;
        init => _surfaceId = InteractionContractGuard.NotBlank(value, nameof(SurfaceId));
    }

    public required string ProviderId
    {
        get => _providerId;
        init => _providerId = InteractionContractGuard.NotBlank(value, nameof(ProviderId));
    }

    public required string ProviderInstanceId
    {
        get => _providerInstanceId;
        init => _providerInstanceId = InteractionContractGuard.NotBlank(value, nameof(ProviderInstanceId));
    }

    public required string SourceId
    {
        get => _sourceId;
        init => _sourceId = InteractionContractGuard.NotBlank(value, nameof(SourceId));
    }

    public required InteractionPhase Phase
    {
        get => _phase;
        init => _phase = InteractionContractGuard.Defined(value, nameof(Phase));
    }

    public required Vector2Data NormalizedPosition
    {
        get => _normalizedPosition;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            InteractionContractGuard.UnitInterval(value.X, nameof(NormalizedPosition));
            InteractionContractGuard.UnitInterval(value.Y, nameof(NormalizedPosition));
            _normalizedPosition = value;
        }
    }

    public required Vector2Data PixelPosition
    {
        get => _pixelPosition;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _pixelPosition = value;
        }
    }

    public required float Confidence
    {
        get => _confidence;
        init => _confidence = InteractionContractGuard.UnitInterval(value, nameof(Confidence));
    }

    public required long TimestampUnixMs { get; init; }

    public InteractionExtensions? Extensions { get; init; }
}

public sealed record InteractionFrame
{
    private string _providerId = null!;
    private string _providerInstanceId = null!;
    private string _surfaceId = null!;
    private IReadOnlyList<InteractionPoint> _points = null!;

    public required string ProviderId
    {
        get => _providerId;
        init => _providerId = InteractionContractGuard.NotBlank(value, nameof(ProviderId));
    }

    public required string ProviderInstanceId
    {
        get => _providerInstanceId;
        init => _providerInstanceId = InteractionContractGuard.NotBlank(value, nameof(ProviderInstanceId));
    }

    public required string SurfaceId
    {
        get => _surfaceId;
        init => _surfaceId = InteractionContractGuard.NotBlank(value, nameof(SurfaceId));
    }

    public required long Sequence { get; init; }

    public required long TimestampUnixMs { get; init; }

    public required IReadOnlyList<InteractionPoint> Points
    {
        get => _points;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var snapshot = value.ToArray();
            if (snapshot.Any(static point => point is null))
            {
                throw new ArgumentException("The point collection cannot contain null elements.", nameof(value));
            }

            _points = Array.AsReadOnly(snapshot);
        }
    }
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
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new JsonException($"The {typeof(T).Name} JSON payload is empty or invalid.");
        }
        catch (ArgumentException exception)
        {
            throw new JsonException($"The {typeof(T).Name} JSON payload violates the interaction contract.", exception);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

internal static class InteractionContractGuard
{
    internal static string NotBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be null, empty, or whitespace.", parameterName);
        }

        return value;
    }

    internal static int Positive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The value must be positive.");
        }

        return value;
    }

    internal static float Finite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The value must be finite.");
        }

        return value;
    }

    internal static float UnitInterval(float value, string parameterName)
    {
        Finite(value, parameterName);
        if (value < 0f || value > 1f)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The value must be between zero and one.");
        }

        return value;
    }

    internal static TEnum Defined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The enum value is not defined.");
        }

        return value;
    }
}

internal sealed class Vector2DataJsonConverter : JsonConverter<Vector2Data>
{
    public override Vector2Data Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A two-dimensional position must be a JSON object.");
        }

        var hasX = false;
        var hasY = false;
        var x = 0f;
        var y = 0f;
        var propertyComparison = options.PropertyNameCaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (!hasX || !hasY)
                {
                    throw new JsonException("A two-dimensional position must explicitly contain both x and y.");
                }

                return new Vector2Data(x, y);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("A two-dimensional position contains an invalid JSON token.");
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("A two-dimensional position ended before its property value.");
            }

            if (string.Equals(propertyName, "x", propertyComparison))
            {
                if (hasX || reader.TokenType != JsonTokenType.Number || !reader.TryGetSingle(out x))
                {
                    throw new JsonException("The x coordinate must be one finite JSON number.");
                }

                hasX = true;
            }
            else if (string.Equals(propertyName, "y", propertyComparison))
            {
                if (hasY || reader.TokenType != JsonTokenType.Number || !reader.TryGetSingle(out y))
                {
                    throw new JsonException("The y coordinate must be one finite JSON number.");
                }

                hasY = true;
            }
            else
            {
                reader.Skip();
            }
        }

        throw new JsonException("A two-dimensional position JSON object was not terminated.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        Vector2Data value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteEndObject();
    }
}

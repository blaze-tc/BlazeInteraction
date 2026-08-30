using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blaze.Interaction.Contracts;

[JsonConverter(typeof(InteractionExtensionsJsonConverter))]
public sealed class InteractionExtensions : IReadOnlyDictionary<string, JsonElement>
{
    private readonly IReadOnlyDictionary<string, JsonElement> _values;

    public InteractionExtensions(IEnumerable<KeyValuePair<string, JsonElement>> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            copy.Add(pair.Key, pair.Value.Clone());
        }

        _values = copy;
    }

    public JsonElement this[string key] => _values[key];

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<JsonElement> Values => _values.Values;

    public int Count => _values.Count;

    public bool ContainsKey(string key)
    {
        return _values.ContainsKey(key);
    }

    public IEnumerator<KeyValuePair<string, JsonElement>> GetEnumerator()
    {
        return _values.GetEnumerator();
    }

    public bool TryGetValue(string key, out JsonElement value)
    {
        return _values.TryGetValue(key, out value);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public sealed record RadarInteractionExtension
{
    [JsonConstructor]
    public RadarInteractionExtension(string sensorId)
    {
        SensorId = InteractionContractGuard.NotBlank(sensorId, nameof(sensorId));
    }

    public string SensorId { get; }
}

public sealed record HandLandmarkExtension
{
    [JsonConstructor]
    public HandLandmarkExtension(
        int index,
        Vector2Data normalizedPosition,
        Vector2Data pixelPosition,
        float z)
    {
        if (index is < 0 or >= HandInteractionExtension.LandmarkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        Index = index;
        NormalizedPosition = normalizedPosition
            ?? throw new ArgumentNullException(nameof(normalizedPosition));
        PixelPosition = pixelPosition
            ?? throw new ArgumentNullException(nameof(pixelPosition));
        Z = InteractionContractGuard.Finite(z, nameof(z));
    }

    public int Index { get; }

    public Vector2Data NormalizedPosition { get; }

    public Vector2Data PixelPosition { get; }

    public float Z { get; }
}

public sealed record HandInteractionExtension
{
    public const int CurrentSchemaVersion = 1;

    public const int LandmarkCount = 21;

    [JsonConstructor]
    public HandInteractionExtension(
        int schemaVersion,
        string trackingPoint,
        IReadOnlyList<HandLandmarkExtension> landmarks)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                $"Only hand extension schema {CurrentSchemaVersion} is supported.");
        }

        ArgumentNullException.ThrowIfNull(landmarks);
        var snapshot = landmarks.ToArray();
        if (snapshot.Length != LandmarkCount)
        {
            throw new ArgumentException(
                $"A hand extension requires exactly {LandmarkCount} landmarks.",
                nameof(landmarks));
        }

        for (var index = 0; index < snapshot.Length; index++)
        {
            var landmark = snapshot[index];
            if (landmark is null)
            {
                throw new ArgumentException("Hand landmarks cannot contain null elements.", nameof(landmarks));
            }

            if (landmark.Index != index)
            {
                throw new ArgumentException(
                    $"Hand landmark at position {index} must have index {index}.",
                    nameof(landmarks));
            }
        }

        SchemaVersion = schemaVersion;
        TrackingPoint = InteractionContractGuard.NotBlank(trackingPoint, nameof(trackingPoint));
        Landmarks = Array.AsReadOnly(snapshot);
    }

    public int SchemaVersion { get; }

    public string TrackingPoint { get; }

    public IReadOnlyList<HandLandmarkExtension> Landmarks { get; }
}

public static class InteractionPointExtensionHelpers
{
    public static bool TryGetRadarExtension(
        this InteractionPoint point,
        out RadarInteractionExtension? extension)
    {
        ArgumentNullException.ThrowIfNull(point);
        return TryDeserialize(point.Extensions, "radar", out extension);
    }

    public static bool TryGetHandExtension(
        this InteractionPoint point,
        out HandInteractionExtension? extension)
    {
        ArgumentNullException.ThrowIfNull(point);
        return TryDeserialize(point.Extensions, "hand", out extension);
    }

    private static bool TryDeserialize<T>(
        InteractionExtensions? extensions,
        string key,
        out T? extension)
        where T : class
    {
        extension = null;
        if (extensions is null || !extensions.TryGetValue(key, out var raw))
        {
            return false;
        }

        try
        {
            extension = raw.Deserialize<T>(InteractionJson.Options);
            return extension is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

internal sealed class InteractionExtensionsJsonConverter : JsonConverter<InteractionExtensions>
{
    public override InteractionExtensions Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Interaction extensions must be a JSON object.");
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        return new InteractionExtensions(values);
    }

    public override void Write(
        Utf8JsonWriter writer,
        InteractionExtensions value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var pair in value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(pair.Key);
            pair.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}

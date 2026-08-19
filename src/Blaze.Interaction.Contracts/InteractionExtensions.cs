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

public sealed record RadarInteractionExtension(string SensorId);

public sealed record HandInteractionExtension(
    InteractionHandedness Handedness,
    string TrackingPoint);

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

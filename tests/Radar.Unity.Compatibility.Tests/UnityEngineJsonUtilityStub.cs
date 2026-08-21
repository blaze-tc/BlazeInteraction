using System.Text.Json;

namespace UnityEngine;

internal static class JsonUtility
{
    internal static T? FromJson<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        });
}

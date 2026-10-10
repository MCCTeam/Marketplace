using System.Text.Json;

namespace LlmCore.Api;

/// <summary>
/// Shared JSON helper with one set of defaults, so every plugin builds schemas and payloads from plain objects instead of hand-writing escaped JSON.
/// </summary>
public static class LLMJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static string Encode(object? value)
        => JsonSerializer.Serialize(value, Options);

    public static T? Decode<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options);
}

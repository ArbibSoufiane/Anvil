using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anvil.Core.IO;

/// <summary>One JSON style for every file under data\ and runtime\: camelCase, enums as strings, indented.</summary>
public static class JsonFiles
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);

    public static void Write<T>(string path, T value) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, Options));
}

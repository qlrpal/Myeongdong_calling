using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildTools;

internal sealed record PackageFile([property: JsonRequired] string Path, [property: JsonRequired] long Bytes, [property: JsonRequired] string Sha256);
internal sealed record PackageManifest
{
    public required int SchemaVersion { get; init; }
    public required string Channel { get; init; }
    public required string Version { get; init; }
    public required string Rid { get; init; }
    public required bool SelfContained { get; init; }
    public required string CreatedUtc { get; init; }
    public string? SourceHead { get; init; }
    public bool? SourceDirty { get; init; }
    public required JsonElement Sdk { get; init; }
    public required JsonElement Codec { get; init; }
    public required PackageFile[] Files { get; init; }
}
internal static class PackageJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict };
    internal static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException("Missing JSON content: " + path);
    internal static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Options) + "\n");
}

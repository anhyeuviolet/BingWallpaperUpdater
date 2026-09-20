using System.Text.Json.Serialization;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Json;

/// <summary>
/// Source-generated System.Text.Json context for every JSON document the app reads or writes.
/// No reflection-based serialization anywhere in the app (CLAUDE.md "What NOT to Use").
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CacheIndex))]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(AppState))]
public sealed partial class CoreJsonContext : JsonSerializerContext
{
}

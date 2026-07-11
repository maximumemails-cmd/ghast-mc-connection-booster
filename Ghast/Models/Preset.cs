using System.Text.Json.Serialization;

namespace Ghast.Models;

public class Preset
{
    /// <summary>
    /// Preset schema version for shared .ghast files. Missing (old exports) deserializes
    /// as 0 and is treated as v1; imports never trust fields blindly (ConfigService.Sanitize).
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("config")]
    public GhastConfig Config { get; set; } = new();

    [JsonIgnore]
    public string? FilePath { get; set; }

    /// <summary>True for the baked-in presets — set on load by name, protected from deletion.</summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }
}

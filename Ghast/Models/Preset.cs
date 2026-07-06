using System.Text.Json.Serialization;

namespace Ghast.Models;

public class Preset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("config")]
    public GhastConfig Config { get; set; } = new();

    [JsonIgnore]
    public string? FilePath { get; set; }
}

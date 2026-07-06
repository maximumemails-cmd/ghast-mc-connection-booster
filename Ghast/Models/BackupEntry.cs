using System.Text.Json.Serialization;

namespace Ghast.Models;

/// <summary>
/// One captured "before" value. The first entry recorded for a key is the true original
/// and is never overwritten (spec §5.2).
/// </summary>
public class BackupEntry
{
    /// <summary>Unique id, e.g. "reg::SYSTEM\...\Interfaces\{guid}::TcpAckFrequency".</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>registry | registrykey | netsh-autotuning | netsh-congestion | mtu | dns</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "registry";

    /// <summary>Registry key path (HKLM-relative), or adapter name for netsh/dns entries.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>Registry value name, adapter GUID for dns entries, or empty.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Original value serialized to string (JSON dictionary for whole-subkey backups).</summary>
    [JsonPropertyName("originalValue")]
    public string? OriginalValue { get; set; }

    /// <summary>RegistryValueKind name for registry entries (DWord, String, ...).</summary>
    [JsonPropertyName("valueKind")]
    public string? ValueKind { get; set; }

    /// <summary>false = the value/key did not exist before Ghast wrote it; restore = delete it.</summary>
    [JsonPropertyName("existedBefore")]
    public bool ExistedBefore { get; set; }

    /// <summary>
    /// For value-level registry entries only: false = Ghast created the parent key too,
    /// so restore also removes the key once it is empty again. Null for non-registry entries.
    /// </summary>
    [JsonPropertyName("keyExistedBefore")]
    public bool? KeyExistedBefore { get; set; }
}

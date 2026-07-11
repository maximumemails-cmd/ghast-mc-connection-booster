using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ghast.Models;

/// <summary>
/// The full serializable settings object (spec §6). Presets are a named copy of this.
/// </summary>
public class GhastConfig
{
    [JsonPropertyName("tier")]
    public string Tier { get; set; } = "Lightning";

    [JsonPropertyName("settings")]
    public SettingsSection Settings { get; set; } = new();

    [JsonPropertyName("advanced")]
    public AdvancedSection Advanced { get; set; } = new();

    /// <summary>none | cloudflare | google</summary>
    [JsonPropertyName("dns")]
    public string Dns { get; set; } = "none";

    /// <summary>
    /// Snapshot of the fields Competitive Mode overrides, taken when it is switched ON,
    /// so switching it OFF restores the user's previous choices. (Schema addition, see README.)
    /// </summary>
    [JsonPropertyName("competitiveSnapshot")]
    public CompetitiveSnapshot? CompetitiveSnapshot { get; set; }

    /// <summary>True once the one-time welcome dialog has been shown. (Schema addition, see README.)</summary>
    [JsonPropertyName("firstRunDone")]
    public bool FirstRunDone { get; set; }

    /// <summary>
    /// Per-server profile: the Ping-tab server (host or host:port) saved with the config —
    /// and therefore with every preset. (Schema addition, see README.)
    /// </summary>
    [JsonPropertyName("pingTarget")]
    public string PingTarget { get; set; } = "";

    public GhastConfig Clone() =>
        JsonSerializer.Deserialize<GhastConfig>(JsonSerializer.Serialize(this)) ?? new GhastConfig();
}

public class SettingsSection
{
    [JsonPropertyName("smartPackets")]
    public bool SmartPackets { get; set; } = true;

    /// <summary>0-4. Merged with Advanced.PacketsDelay into one TcpDelAckTicks value (see README).</summary>
    [JsonPropertyName("latency")]
    public int Latency { get; set; } = 0;

    /// <summary>0-20 slider value; the registry value written is (20 - this).</summary>
    [JsonPropertyName("responsiveness")]
    public int Responsiveness { get; set; } = 20;

    /// <summary>Disabled | HighlyRestricted | Restricted | Normal | Experimental</summary>
    [JsonPropertyName("tuning")]
    public string Tuning { get; set; } = "Restricted";

    /// <summary>Fiber | Cable | DSL | Satellite | WiFi</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Fiber";

    [JsonPropertyName("connectionStable")]
    public bool ConnectionStable { get; set; } = true;

    [JsonPropertyName("competitiveMode")]
    public bool CompetitiveMode { get; set; } = true;
}

public class AdvancedSection
{
    [JsonPropertyName("mtuAutomatic")]
    public bool MtuAutomatic { get; set; } = true;

    /// <summary>Used only when MtuAutomatic is false. 576-1500.</summary>
    [JsonPropertyName("mtuValue")]
    public int MtuValue { get; set; } = 1500;

    /// <summary>0-6. Authoritative source for TcpDelAckTicks: ticks = 6 - packetsDelay... inverted, see README.</summary>
    [JsonPropertyName("packetsDelay")]
    public int PacketsDelay { get; set; } = 4;

    /// <summary>0-5. Scales the QoS DSCP policy + game process priority.</summary>
    [JsonPropertyName("networkPriority")]
    public int NetworkPriority { get; set; } = 1;

    /// <summary>Default | CUBIC | CTCP | NewReno | DCTCP</summary>
    [JsonPropertyName("congestionProvider")]
    public string CongestionProvider { get; set; } = "Default";

    [JsonPropertyName("ghastPriorityMode")]
    public bool GhastPriorityMode { get; set; } = true;

    /// <summary>true = adapter power saving DISABLED (the toggle reads "handle it").</summary>
    [JsonPropertyName("networkPowerSaving")]
    public bool NetworkPowerSaving { get; set; } = true;
}

public class CompetitiveSnapshot
{
    [JsonPropertyName("smartPackets")]
    public bool SmartPackets { get; set; }

    [JsonPropertyName("responsiveness")]
    public int Responsiveness { get; set; }

    [JsonPropertyName("ghastPriorityMode")]
    public bool GhastPriorityMode { get; set; }

    [JsonPropertyName("networkPowerSaving")]
    public bool NetworkPowerSaving { get; set; }
}

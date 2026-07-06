using System.IO;
using System.Text.Json;
using Ghast.Models;

namespace Ghast.Services;

/// <summary>Loads/saves the last-used settings at %AppData%\Ghast\config.json.</summary>
public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public GhastConfig Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigPath))
            {
                var config = JsonSerializer.Deserialize<GhastConfig>(File.ReadAllText(Paths.ConfigPath));
                if (config is not null)
                    return Sanitize(config);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("loading config.json", ex);
        }
        return new GhastConfig();
    }

    public void Save(GhastConfig config)
    {
        try
        {
            Paths.EnsureCreated();
            File.WriteAllText(Paths.ConfigPath, JsonSerializer.Serialize(config, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Error("saving config.json", ex);
        }
    }

    /// <summary>Clamps out-of-range values from hand-edited or imported files.</summary>
    public static GhastConfig Sanitize(GhastConfig c)
    {
        c.Settings.Latency = Math.Clamp(c.Settings.Latency, 0, 4);
        c.Settings.Responsiveness = Math.Clamp(c.Settings.Responsiveness, 0, 20);
        c.Advanced.PacketsDelay = Math.Clamp(c.Advanced.PacketsDelay, 0, 6);
        c.Advanced.NetworkPriority = Math.Clamp(c.Advanced.NetworkPriority, 0, 5);
        c.Advanced.MtuValue = Math.Clamp(c.Advanced.MtuValue, 576, 1500);

        // Canonicalize casing too: the ComboBoxes select by exact SelectedItem match,
        // so "restricted" from a hand-edited preset must become "Restricted".
        string[] tunings = { "Disabled", "HighlyRestricted", "Restricted", "Normal", "Experimental" };
        c.Settings.Tuning = Canonical(tunings, c.Settings.Tuning, "Normal");

        string[] types = { "Fiber", "Cable", "DSL", "Satellite", "WiFi" };
        c.Settings.Type = Canonical(types, c.Settings.Type, "Fiber");

        string[] providers = { "Default", "CUBIC", "CTCP", "NewReno", "DCTCP" };
        c.Advanced.CongestionProvider = Canonical(providers, c.Advanced.CongestionProvider, "Default");

        string[] dns = { "none", "cloudflare", "google" };
        c.Dns = Canonical(dns, c.Dns, "none");

        if (string.IsNullOrWhiteSpace(c.Tier))
            c.Tier = "Lightning";

        return c;
    }

    private static string Canonical(string[] options, string? value, string fallback) =>
        options.FirstOrDefault(o => o.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? fallback;
}

using System.IO;
using System.Text.Json;
using Ghast.Models;

namespace Ghast.Services;

/// <summary>
/// One JSON file per preset at %AppData%\Ghast\presets\*.ghast, plus a small _order.json
/// so drag-reordering survives restarts. Seeds the four demo presets on first launch.
/// </summary>
public class PresetService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static string OrderPath => Path.Combine(Paths.PresetsDir, "_order.json");

    public List<Preset> LoadAll()
    {
        Paths.EnsureCreated();
        var presets = new List<Preset>();
        foreach (var file in Directory.EnumerateFiles(Paths.PresetsDir, "*.ghast"))
        {
            try
            {
                var preset = JsonSerializer.Deserialize<Preset>(File.ReadAllText(file));
                if (preset is not null && !string.IsNullOrWhiteSpace(preset.Name))
                {
                    preset.Config = ConfigService.Sanitize(preset.Config);
                    preset.FilePath = file;
                    preset.IsBuiltIn = BuiltInNames.Contains(preset.Name);
                    presets.Add(preset);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"loading preset {file}", ex);
            }
        }

        // Apply saved ordering; unknown names go to the end in file order.
        var order = LoadOrder();
        return presets
            .OrderBy(p => { var i = order.IndexOf(p.Name); return i < 0 ? int.MaxValue : i; })
            .ToList();
    }

    public void Save(Preset preset)
    {
        Paths.EnsureCreated();
        var baseName = SanitizeFileName(preset.Name);
        var path = Path.Combine(Paths.PresetsDir, baseName + ".ghast");

        // Different preset names can sanitize to the same file name ("A/B" and "A_B") —
        // never clobber a file that holds a different preset.
        for (var n = 2; File.Exists(path) && !FileHoldsPreset(path, preset.Name); n++)
            path = Path.Combine(Paths.PresetsDir, $"{baseName}~{n}.ghast");

        File.WriteAllText(path, JsonSerializer.Serialize(preset, JsonOpts));
        preset.FilePath = path;
        Logger.Log($"preset saved: {preset.Name} -> {Path.GetFileName(path)}");
    }

    private static bool FileHoldsPreset(string path, string name)
    {
        try
        {
            var existing = JsonSerializer.Deserialize<Preset>(File.ReadAllText(path));
            return existing is not null
                   && existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Delete(Preset preset)
    {
        try
        {
            if (preset.FilePath is not null && File.Exists(preset.FilePath))
                File.Delete(preset.FilePath);
            Logger.Log($"preset deleted: {preset.Name}");
        }
        catch (Exception ex)
        {
            Logger.Error($"deleting preset {preset.Name}", ex);
        }
    }

    /// <summary>Validates the file parses as a Preset (or bare GhastConfig) before copying it in.</summary>
    public Preset Import(string sourcePath)
    {
        var text = File.ReadAllText(sourcePath);

        // Structural check first: arbitrary JSON would otherwise deserialize into an
        // all-defaults preset, making "validate schema" a no-op.
        bool looksLikePreset, looksLikeConfig;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            looksLikePreset = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                && root.TryGetProperty("config", out var cfgEl) && cfgEl.ValueKind == JsonValueKind.Object;
            looksLikeConfig = root.ValueKind == JsonValueKind.Object
                && ((root.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
                    || (root.TryGetProperty("advanced", out var a) && a.ValueKind == JsonValueKind.Object));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Not a valid Ghast preset file: {ex.Message}");
        }

        if (!looksLikePreset && !looksLikeConfig)
            throw new InvalidOperationException(
                "Not a valid Ghast preset file (expected a preset with \"name\" + \"config\", or a bare Ghast config).");

        Preset? preset = null;
        if (looksLikePreset)
            preset = JsonSerializer.Deserialize<Preset>(text);

        if (preset is null || string.IsNullOrWhiteSpace(preset.Name))
        {
            var config = JsonSerializer.Deserialize<GhastConfig>(text)
                         ?? throw new InvalidOperationException("Not a valid Ghast preset file.");
            preset = new Preset
            {
                Name = Path.GetFileNameWithoutExtension(sourcePath),
                Config = config
            };
        }

        preset.Config = ConfigService.Sanitize(preset.Config);

        // Old shared files have no version field (deserializes 0) → normalize to v1.
        if (preset.Version < 1)
            preset.Version = 1;

        // Avoid clobbering an existing preset with the same name.
        var existingNames = LoadAll().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = preset.Name;
        for (var n = 2; existingNames.Contains(preset.Name); n++)
            preset.Name = $"{baseName} ({n})";

        Save(preset);
        return preset;
    }

    /// <summary>Writes a shareable .ghast copy of the preset into the given folder; returns the path.</summary>
    public string ExportTo(Preset preset, string folder)
    {
        var path = Path.Combine(folder, SanitizeFileName(preset.Name) + ".ghast");
        File.WriteAllText(path, JsonSerializer.Serialize(preset, JsonOpts));
        Logger.Log($"preset exported: {preset.Name} -> {path}");
        return path;
    }

    public void SaveOrder(IEnumerable<string> names)
    {
        try
        {
            Paths.EnsureCreated();
            File.WriteAllText(OrderPath, JsonSerializer.Serialize(names.ToList(), JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Error("saving preset order", ex);
        }
    }

    private static List<string> LoadOrder()
    {
        try
        {
            if (File.Exists(OrderPath))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(OrderPath)) ?? new();
        }
        catch (Exception ex)
        {
            Logger.Error("loading preset order", ex);
        }
        return new();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "preset" : clean;
    }

    /// <summary>
    /// Ensures the baked-in presets exist. On first launch it seeds the four demo presets plus
    /// the eight built-ins; for existing users it adds only any missing built-ins (so upgrades
    /// gain them, and deleting a built-in re-creates it — they are protected baked-ins).
    /// </summary>
    public void EnsureSeeded()
    {
        Paths.EnsureCreated();
        var hasFiles = Directory.EnumerateFiles(Paths.PresetsDir, "*.ghast").Any();
        var demos = DemoPresets();
        var builtins = BuiltInPresets();

        var existing = hasFiles
            ? LoadAll().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var toAdd = new List<Preset>();
        if (!hasFiles)
            toAdd.AddRange(demos.Where(d => !existing.Contains(d.Name)));
        toAdd.AddRange(builtins.Where(b => !existing.Contains(b.Name)));

        if (toAdd.Count == 0)
            return;

        foreach (var preset in toAdd)
            Save(preset);

        // First launch: define the whole order. Otherwise append the newly added built-ins.
        var order = LoadOrder();
        if (order.Count == 0)
            order = demos.Select(p => p.Name).Concat(builtins.Select(p => p.Name)).ToList();
        else
            order.AddRange(toAdd.Select(a => a.Name).Where(n => !order.Contains(n)));
        SaveOrder(order);
        Logger.Log($"seeded {toAdd.Count} preset(s)");
    }

    // ---------- built-in preset catalogue ----------

    /// <summary>Names of the eight baked-in presets (protected from deletion, shown in Explain).</summary>
    public static readonly IReadOnlySet<string> BuiltInNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Best Hit-Reg", "Best KB", "1.8.9 Balanced", "Modern Balanced",
        "Competitive Max", "Stable Wi-Fi", "BedWars Rush", "High-Ping Fix"
    };

    /// <summary>The original four demo presets — kept exactly as before, deletable.</summary>
    private static List<Preset> DemoPresets() => new()
    {
        new()
        {
            Name = "Nodebuff MMC",
            Config = new GhastConfig
            {
                Settings = new SettingsSection
                {
                    SmartPackets = true, Latency = 4, Responsiveness = 20,
                    Tuning = "Normal", Type = "Fiber", ConnectionStable = true, CompetitiveMode = true
                },
                Advanced = new AdvancedSection
                {
                    MtuAutomatic = true, MtuValue = 1500, PacketsDelay = 6, NetworkPriority = 5,
                    CongestionProvider = "CTCP", GhastPriorityMode = true, NetworkPowerSaving = true
                },
                Dns = "cloudflare"
            }
        },
        new()
        {
            Name = "Sumo",
            Config = new GhastConfig
            {
                Settings = new SettingsSection
                {
                    SmartPackets = true, Latency = 3, Responsiveness = 16,
                    Tuning = "Normal", Type = "Fiber", ConnectionStable = true, CompetitiveMode = false
                },
                Advanced = new AdvancedSection
                {
                    MtuAutomatic = true, MtuValue = 1500, PacketsDelay = 6, NetworkPriority = 4,
                    CongestionProvider = "CUBIC", GhastPriorityMode = true, NetworkPowerSaving = true
                },
                Dns = "none"
            }
        },
        new()
        {
            Name = "FanCraft Rush 1.9",
            Config = new GhastConfig
            {
                Settings = new SettingsSection
                {
                    SmartPackets = true, Latency = 1, Responsiveness = 12,
                    Tuning = "Restricted", Type = "Cable", ConnectionStable = true, CompetitiveMode = false
                },
                Advanced = new AdvancedSection
                {
                    MtuAutomatic = true, MtuValue = 1500, PacketsDelay = 5, NetworkPriority = 3,
                    CongestionProvider = "Default", GhastPriorityMode = true, NetworkPowerSaving = true
                },
                Dns = "none"
            }
        },
        new()
        {
            Name = "Build UHC 1.8",
            Config = new GhastConfig
            {
                Settings = new SettingsSection
                {
                    SmartPackets = false, Latency = 0, Responsiveness = 6,
                    Tuning = "Restricted", Type = "DSL", ConnectionStable = false, CompetitiveMode = false
                },
                Advanced = new AdvancedSection
                {
                    MtuAutomatic = false, MtuValue = 1500, PacketsDelay = 4, NetworkPriority = 1,
                    CongestionProvider = "Default", GhastPriorityMode = false, NetworkPowerSaving = false
                },
                Dns = "none"
            }
        }
    };

    // FLAG: polarity — NetworkPowerSaving == true means "adapter power management DISABLED"
    // (the low-latency state the follow-up table calls NIC Power "OFF"). All built-ins use true.
    // FLAG: the table's Latency (0–4) is mapped onto the authoritative PacketsDelay via
    // PacketsDelay = 6 - (table ticks): L4→PD6 (delayed-ACK off), L3→PD5, L2→PD4. The Settings
    // "Latency" slider is a coarse mirror and is recomputed from PacketsDelay on load.
    // FLAG: ConnectionStable is left true on all (incl. Stable Wi-Fi) so the explicit Tuning
    // value isn't clamped to Normal by the unstable-connection safeguard.
    private static Preset BuiltIn(string name, bool smartPackets, int latency, int responsiveness,
        string tuning, bool competitive, string congestion, int netPriority, string type) => new()
    {
        Name = name,
        IsBuiltIn = true,
        Config = new GhastConfig
        {
            Settings = new SettingsSection
            {
                SmartPackets = smartPackets, Latency = latency, Responsiveness = responsiveness,
                Tuning = tuning, Type = type, ConnectionStable = true, CompetitiveMode = competitive
            },
            Advanced = new AdvancedSection
            {
                MtuAutomatic = true, MtuValue = 1500,
                PacketsDelay = Math.Clamp(6 - (4 - latency), 0, 6), // table Latency → ticks → PacketsDelay
                NetworkPriority = netPriority,
                CongestionProvider = congestion,
                GhastPriorityMode = true,      // Priority ON on every built-in
                NetworkPowerSaving = true      // NIC power management disabled (low-latency)
            },
            Dns = "none"
        }
    };

    /// <summary>The eight baked-in presets from the follow-up spec table.</summary>
    private static List<Preset> BuiltInPresets() => new()
    {
        //     name                smart  lat resp  tuning        comp   congestion  netPri type
        BuiltIn("Best Hit-Reg",    true,  4,  20,   "Normal",     true,  "Default",  5,     "Fiber"),
        BuiltIn("Best KB",         true,  4,  20,   "Normal",     true,  "CTCP",     5,     "Fiber"),
        BuiltIn("1.8.9 Balanced",  true,  3,  18,   "Normal",     true,  "Default",  4,     "Fiber"),
        BuiltIn("Modern Balanced", true,  3,  16,   "Normal",     false, "Default",  4,     "Fiber"),
        BuiltIn("Competitive Max", true,  4,  20,   "Normal",     true,  "CTCP",     5,     "Fiber"),
        BuiltIn("Stable Wi-Fi",    true,  2,  15,   "Restricted", false, "Default",  4,     "WiFi"),
        BuiltIn("BedWars Rush",    true,  4,  20,   "Normal",     true,  "Default",  5,     "Fiber"),
        BuiltIn("High-Ping Fix",   true,  3,  16,   "Normal",     false, "CTCP",     3,     "Satellite")
    };

    /// <summary>Honest header + one-liners for the "What do these do?" popup.</summary>
    public const string BuiltInIntro =
        "These presets tune your PC's connection — jitter, delay Windows adds, and CPU priority. " +
        "They don't change server-side things like actual knockback or tick rate. Several presets " +
        "share most settings because the real gains come from connection quality, not the game version.";

    public static IReadOnlyList<PresetExplanation> Explanations { get; } = new List<PresetExplanation>
    {
        new("Best Hit-Reg", "Rawest low-delay setup: Nagle off, delayed-ACK off, max responsiveness, game process prioritised, NIC power saving off. Sends your attack packets instantly with the least self-inflicted jitter, so hits register cleanly. Limit: hit-reg is mostly ping + server tick + your FPS — this removes Windows-added delay, it can't fix a laggy server."),
        new("Best KB", "Same low-delay base, tuned for the steadiest packet flow (steadier congestion control). Makes knockback feel consistent instead of arriving in laggy clumps. Limit: the amount of knockback is 100% server-side — no PC setting increases it. This only affects consistency."),
        new("1.8.9 Balanced", "For classic click-spam PvP (no attack cooldown). Fast, steady packet delivery without maxing every dial, so it stays stable on normal connections."),
        new("Modern Balanced", "For 1.9+ combat (attack cooldown, sweep). Timing beats spam here, so it favours consistency and low jitter over raw aggression."),
        new("Competitive Max", "Everything cranked: lowest self-inflicted delay, max responsiveness, top priority. Best on a stable wired connection; can feel less smooth on a flaky one."),
        new("Stable Wi-Fi", "For wireless / flaky connections. Turns off NIC power saving (the big Wi-Fi latency-spike culprit) and uses restricted TCP tuning to keep things steady rather than fast."),
        new("BedWars Rush", "Fast-and-loose for Hypixel-style rush games: instant small packets, max responsiveness, high priority. Built for quick bridging and spammy fights."),
        new("High-Ping Fix", "For distant servers. Keeps TCP auto-tuning open so the receive window can grow for high-latency links, and steadies congestion control so throughput doesn't collapse.")
    };
}

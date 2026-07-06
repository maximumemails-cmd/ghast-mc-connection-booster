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

        // Avoid clobbering an existing preset with the same name.
        var existingNames = LoadAll().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = preset.Name;
        for (var n = 2; existingNames.Contains(preset.Name); n++)
            preset.Name = $"{baseName} ({n})";

        Save(preset);
        return preset;
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

    /// <summary>First-launch demo presets — deliberately different from each other (spec §9).</summary>
    public void EnsureSeeded()
    {
        Paths.EnsureCreated();
        if (Directory.EnumerateFiles(Paths.PresetsDir, "*.ghast").Any() || File.Exists(OrderPath))
            return;

        var seeds = new List<Preset>
        {
            new()
            {
                // Full aggressive PvP profile.
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
                // Aggressive but no competitive bundle and no DNS change.
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
                // Balanced middle ground.
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
                // Conservative: unstable-connection clamps on, explicit full-size MTU.
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

        foreach (var preset in seeds)
            Save(preset);
        SaveOrder(seeds.Select(p => p.Name));
        Logger.Log("seeded demo presets");
    }
}

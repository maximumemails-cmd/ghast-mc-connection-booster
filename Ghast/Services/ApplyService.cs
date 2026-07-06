using System.Diagnostics;
using Ghast.Models;

namespace Ghast.Services;

/// <summary>
/// Orchestrates a full "Run" (spec §7): back up → write, in a fixed order, collecting
/// per-item results instead of failing the whole pass. Also owns Restore Defaults.
/// </summary>
public class ApplyService
{
    private const string MultimediaKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string GamesTaskKey = MultimediaKey + @"\Tasks\Games";
    private const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    private readonly RegistryService _registry;
    private readonly NetshService _netsh;
    private readonly QosService _qos;
    private readonly AdapterService _adapters;
    private readonly ProcessPriorityService _process;
    private readonly BackupService _backup;
    private readonly ConfigService _configService;

    public ApplyService(RegistryService registry, NetshService netsh, QosService qos,
        AdapterService adapters, ProcessPriorityService process, BackupService backup,
        ConfigService configService)
    {
        _registry = registry;
        _netsh = netsh;
        _qos = qos;
        _adapters = adapters;
        _process = process;
        _backup = backup;
        _configService = configService;
    }

    // ---------- delayed-ACK merge (see README: Latency and Packets Delay share TcpDelAckTicks) ----------

    /// <summary>Advanced.PacketsDelay is authoritative: slider 0→6 ticks ... 6→0 ticks.</summary>
    public static int TicksFromPacketsDelay(int packetsDelay) => Math.Clamp(6 - packetsDelay, 0, 6);

    /// <summary>Settings.Latency mirror: 0→2 (default), 1→1, 2..4→0.</summary>
    public static int TicksFromLatency(int latency) => latency switch { 0 => 2, 1 => 1, _ => 0 };

    public static int LatencyFromTicks(int ticks) => ticks switch { >= 2 => 0, 1 => 1, _ => 2 };

    public static int PacketsDelayFromTicks(int ticks) => Math.Clamp(6 - ticks, 0, 6);

    // ---------- plan preview for the confirm dialog ----------

    public List<string> BuildPlan(GhastConfig config)
    {
        var c = config.Clone();
        var clamped = !c.Settings.ConnectionStable;
        var plan = new List<string>();
        var adapterCount = _adapters.GetActiveAdapters().Count;

        if (clamped)
            plan.Add("Connection marked unstable — risky tweaks are clamped to safe values.");

        var responsivenessValue = 20 - Math.Clamp(c.Settings.Responsiveness, 0, 20);
        plan.Add($"SystemResponsiveness → {responsivenessValue} (multimedia profile)");
        if (c.Settings.CompetitiveMode)
            plan.Add("Competitive Mode: disable network throttling (NetworkThrottlingIndex)");

        if (c.Settings.SmartPackets && !clamped)
            plan.Add($"Smart Packets: disable Nagle + delayed ACK count on {adapterCount} adapter(s)");
        else if (c.Settings.SmartPackets)
            plan.Add("Smart Packets (clamped): Nagle off, delayed ACKs kept");
        else
            plan.Add("Smart Packets OFF: remove TcpAckFrequency / TCPNoDelay (default bundling)");

        if (!clamped)
            plan.Add($"Delayed-ACK timer (TcpDelAckTicks) → {TicksFromPacketsDelay(c.Advanced.PacketsDelay)}");

        plan.Add($"TCP auto-tuning → {(clamped ? "Normal (clamped)" : c.Settings.Tuning)}");

        if (!clamped && !c.Advanced.CongestionProvider.Equals("Default", StringComparison.OrdinalIgnoreCase))
            plan.Add($"Congestion provider → {c.Advanced.CongestionProvider}");

        if (!c.Advanced.MtuAutomatic && !clamped)
            plan.Add($"MTU → {c.Advanced.MtuValue} on {adapterCount} adapter(s)");
        else
            plan.Add("MTU: automatic (restore original if Ghast changed it)");

        plan.Add(c.Advanced.NetworkPriority > 0
            ? $"QoS DSCP {QosService.DscpForLevel(c.Advanced.NetworkPriority)} policy for javaw.exe / java.exe"
            : "QoS policy: remove");

        plan.Add(c.Advanced.NetworkPowerSaving
            ? $"Disable adapter power saving on {adapterCount} adapter(s)"
            : "Adapter power saving: restore Windows defaults");

        plan.Add(c.Advanced.GhastPriorityMode
            ? "Ghast Priority Mode: game process → High + multimedia Games task boost"
            : $"Game process priority → {ProcessPriorityService.MapNetworkPriority(c.Advanced.NetworkPriority)}");

        if (!c.Dns.Equals("none", StringComparison.OrdinalIgnoreCase))
            plan.Add($"DNS → {(c.Dns == "cloudflare" ? "Cloudflare (1.1.1.1)" : "Google (8.8.8.8)")}");

        return plan;
    }

    // ---------- run ----------

    public async Task<List<ApplyResult>> RunAsync(GhastConfig config, IProgress<ApplyResult>? progress = null)
    {
        var results = new List<ApplyResult>();
        void Report(string item, bool ok, string? msg = null)
        {
            var r = new ApplyResult(item, ok, msg);
            results.Add(r);
            progress?.Report(r);
            if (!ok)
                Logger.Log($"FAILED {item}: {msg}");
        }

        if (!OperatingSystem.IsWindows())
        {
            Report("Run", false, "Ghast can only apply tweaks on Windows.");
            return results;
        }

        var c = config.Clone();
        var clamped = !c.Settings.ConnectionStable;
        var adapters = _adapters.GetActiveAdapters();

        // 1. Multimedia profile keys
        try
        {
            var value = (uint)(20 - Math.Clamp(c.Settings.Responsiveness, 0, 20));
            _registry.SetDword(MultimediaKey, "SystemResponsiveness", value);
            Report("Responsiveness", true, $"SystemResponsiveness = {value}");
        }
        catch (Exception ex) { Report("Responsiveness", false, ex.Message); }

        try
        {
            if (c.Settings.CompetitiveMode)
            {
                _registry.SetDword(MultimediaKey, "NetworkThrottlingIndex", 0xFFFFFFFF);
                Report("Network throttling", true, "disabled (Competitive Mode)");
            }
            else if (RestoreIfBackedUp($"reg::{MultimediaKey}::NetworkThrottlingIndex"))
            {
                Report("Network throttling", true, "restored to original");
            }
        }
        catch (Exception ex) { Report("Network throttling", false, ex.Message); }

        // 2. Per-interface TCP keys
        var interfaceKeys = InterfaceKeysFor(adapters);
        try
        {
            if (c.Settings.SmartPackets && !clamped)
            {
                foreach (var path in interfaceKeys)
                {
                    _registry.SetDword(path, "TcpAckFrequency", 1);
                    _registry.SetDword(path, "TCPNoDelay", 1);
                }
                Report("Smart Packets", true, $"Nagle off on {interfaceKeys.Count} adapter(s)");
            }
            else if (c.Settings.SmartPackets && clamped)
            {
                // Unstable clamp: Nagle off is safe, but delayed ACKs must be KEPT —
                // put back any TcpAckFrequency an earlier run wrote instead of writing 1.
                foreach (var path in interfaceKeys)
                {
                    _registry.SetDword(path, "TCPNoDelay", 1);
                    RestoreIfBackedUp($"reg::{path}::TcpAckFrequency");
                }
                Report("Smart Packets", true, "Nagle off; delayed ACKs kept (unstable clamp)");
            }
            else
            {
                foreach (var path in interfaceKeys)
                {
                    _registry.DeleteValue(path, "TcpAckFrequency");
                    _registry.DeleteValue(path, "TCPNoDelay");
                }
                Report("Smart Packets", true, "default packet bundling restored");
            }
        }
        catch (Exception ex) { Report("Smart Packets", false, ex.Message); }

        try
        {
            if (!clamped)
            {
                var ticks = (uint)TicksFromPacketsDelay(c.Advanced.PacketsDelay);
                foreach (var path in interfaceKeys)
                    _registry.SetDword(path, "TcpDelAckTicks", ticks);
                Report("Delayed ACK timer", true, $"TcpDelAckTicks = {ticks}");
            }
            else
            {
                var restored = interfaceKeys.Count(path => RestoreIfBackedUp($"reg::{path}::TcpDelAckTicks"));
                Report("Delayed ACK timer", true,
                    restored > 0 ? $"restored on {restored} adapter(s) (clamp)" : "left at default (clamp)");
            }
        }
        catch (Exception ex) { Report("Delayed ACK timer", false, ex.Message); }

        // 3. netsh global: autotuning, congestion provider
        try
        {
            var level = clamped ? "Normal" : c.Settings.Tuning;
            await _netsh.SetAutotuningAsync(level);
            Report("TCP auto-tuning", true, level);
        }
        catch (Exception ex) { Report("TCP auto-tuning", false, ex.Message); }

        try
        {
            var provider = clamped ? "Default" : c.Advanced.CongestionProvider;
            var msg = await _netsh.ApplyCongestionProviderAsync(provider);
            Report("Congestion provider", true, msg);
        }
        catch (Exception ex) { Report("Congestion provider", false, ex.Message); }

        // 4. MTU
        try
        {
            if (!c.Advanced.MtuAutomatic && !clamped)
            {
                var mtu = Math.Clamp(c.Advanced.MtuValue, 576, 1500);
                foreach (var adapter in adapters)
                    await _netsh.SetMtuAsync(adapter.Name, mtu);
                Report("MTU", true, $"{mtu} on {adapters.Count} adapter(s)");
            }
            else
            {
                var restored = 0;
                var failed = 0;
                foreach (var adapter in adapters)
                {
                    var entry = _backup.Get($"netsh::mtu::{adapter.Name}");
                    if (entry?.OriginalValue is { } original)
                    {
                        var (code, output) = await _netsh.RunAsync(
                            $"interface ipv4 set subinterface \"{adapter.Name}\" mtu={original} store=persistent");
                        if (code == 0)
                            restored++;
                        else
                        {
                            failed++;
                            Logger.Log($"FAILED restoring MTU on {adapter.Name}: {output}");
                        }
                    }
                }
                Report("MTU", failed == 0,
                    failed > 0 ? $"restore failed on {failed} adapter(s) — see log"
                    : restored > 0 ? $"restored original on {restored} adapter(s)"
                    : "automatic");
            }
        }
        catch (Exception ex) { Report("MTU", false, ex.Message); }

        // 5. QoS / DSCP
        try
        {
            var msg = _qos.Apply(c.Advanced.NetworkPriority);
            Report("QoS priority", true, msg);
        }
        catch (Exception ex) { Report("QoS priority", false, ex.Message); }

        // 6. Adapter power management
        try
        {
            var touched = 0;
            foreach (var adapter in adapters)
            {
                try
                {
                    _adapters.SetPowerSaving(adapter, c.Advanced.NetworkPowerSaving);
                    touched++;
                }
                catch (Exception ex)
                {
                    Logger.Error($"power saving on {adapter.Name}", ex);
                }
            }
            Report("Adapter power saving", touched > 0 || adapters.Count == 0,
                c.Advanced.NetworkPowerSaving
                    ? $"disabled on {touched}/{adapters.Count} adapter(s)"
                    : $"restored on {touched}/{adapters.Count} adapter(s)");
        }
        catch (Exception ex) { Report("Adapter power saving", false, ex.Message); }

        // 7. Process priority + multimedia Games task
        try
        {
            if (c.Advanced.GhastPriorityMode)
            {
                _registry.SetDword(GamesTaskKey, "Priority", 6);
                _registry.SetString(GamesTaskKey, "Scheduling Category", "High");
                _registry.SetString(GamesTaskKey, "SFIO Priority", "High");
                var count = _process.SetPriority(ProcessPriorityClass.High);
                Report("Ghast Priority Mode", true,
                    count > 0 ? $"{count} Minecraft process(es) → High" : "no Minecraft process running (task keys set)");
            }
            else
            {
                RestoreIfBackedUp($"reg::{GamesTaskKey}::Priority");
                RestoreIfBackedUp($"reg::{GamesTaskKey}::Scheduling Category");
                RestoreIfBackedUp($"reg::{GamesTaskKey}::SFIO Priority");
                var target = ProcessPriorityService.MapNetworkPriority(c.Advanced.NetworkPriority);
                var count = _process.SetPriority(target);
                Report("Process priority", true,
                    count > 0 ? $"{count} Minecraft process(es) → {target}" : "no Minecraft process running");
            }
        }
        catch (Exception ex) { Report("Process priority", false, ex.Message); }

        // 8. DNS
        try
        {
            var msg = await _netsh.ApplyDnsAsync(c.Dns, adapters);
            Report("DNS", true, msg);
        }
        catch (Exception ex) { Report("DNS", false, ex.Message); }

        _configService.Save(config);
        Logger.Log($"run complete: {results.Count(r => r.Success)}/{results.Count} ok");
        return results;
    }

    // ---------- restore defaults ----------

    public async Task<List<ApplyResult>> RestoreAllAsync(IProgress<ApplyResult>? progress = null)
    {
        var results = new List<ApplyResult>();
        void Report(string item, bool ok, string? msg = null)
        {
            var r = new ApplyResult(item, ok, msg);
            results.Add(r);
            progress?.Report(r);
        }

        if (!OperatingSystem.IsWindows())
        {
            Report("Restore", false, "Ghast can only restore tweaks on Windows.");
            return results;
        }

        var entries = _backup.Entries;
        if (entries.Count == 0)
        {
            Report("Restore Defaults", true, "nothing to restore — Ghast has not changed anything");
            return results;
        }

        var allOk = true;
        foreach (var entry in entries)
        {
            try
            {
                switch (entry.Type)
                {
                    case "registry":
                    case "registrykey":
                        _registry.RestoreEntry(entry);
                        break;
                    case "netsh-autotuning":
                    {
                        var (code, output) = await _netsh.RunAsync(
                            $"int tcp set global autotuninglevel={entry.OriginalValue}");
                        if (code != 0)
                            throw new InvalidOperationException($"netsh autotuning restore failed: {output}");
                        break;
                    }
                    case "netsh-congestion":
                        await _netsh.SetCongestionRawAsync(entry.OriginalValue ?? "cubic");
                        break;
                    case "mtu":
                    {
                        var (code, output) = await _netsh.RunAsync(
                            $"interface ipv4 set subinterface \"{entry.Path}\" mtu={entry.OriginalValue} store=persistent");
                        if (code != 0)
                            throw new InvalidOperationException($"netsh MTU restore failed: {output}");
                        break;
                    }
                    case "dns":
                        await _netsh.RestoreDnsAsync(entry);
                        break;
                    default:
                        throw new InvalidOperationException($"unknown backup type '{entry.Type}'");
                }
                Report(entry.Key, true);
            }
            catch (Exception ex)
            {
                allOk = false;
                Report(entry.Key, false, ex.Message);
                Logger.Error($"restoring {entry.Key}", ex);
            }
        }

        try
        {
            var count = _process.SetPriority(ProcessPriorityClass.Normal);
            if (count > 0)
                Report("Process priority", true, $"{count} Minecraft process(es) → Normal");
        }
        catch (Exception ex) { Report("Process priority", false, ex.Message); }

        // Only forget the "before" state if every value actually went back.
        if (allOk)
            _backup.Clear();
        else
            Report("Backup store", false, "kept backup.json because some restores failed — fix and retry");

        return results;
    }

    public Task FlushAsync() => _adapters.FlushAsync(_netsh);

    // ---------- helpers ----------

    private List<string> InterfaceKeysFor(IReadOnlyList<AdapterInfo> adapters)
    {
        var subKeys = _registry.GetSubKeyNames(InterfacesKey)
            .ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        foreach (var adapter in adapters)
        {
            if (subKeys.TryGetValue(adapter.Guid, out var actual))
                paths.Add($@"{InterfacesKey}\{actual}");
        }
        return paths;
    }

    private bool RestoreIfBackedUp(string key)
    {
        var entry = _backup.Get(key);
        if (entry is null)
            return false;
        _registry.RestoreEntry(entry);
        return true;
    }
}

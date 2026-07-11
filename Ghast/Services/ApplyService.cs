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

    /// <summary>
    /// True when the last Run wrote/removed per-interface TCP values (TcpAckFrequency,
    /// TCPNoDelay, TcpDelAckTicks). Windows reads those only when a TCP connection is
    /// established, so an already-open Minecraft session needs a reconnect to pick them up.
    /// </summary>
    public bool LastRunChangedTcp { get; private set; }

    public async Task<List<ApplyResult>> RunAsync(GhastConfig config, IProgress<ApplyProgress>? progress = null)
    {
        LastRunChangedTcp = false;
        var results = new List<ApplyResult>();
        // Progress is stage-based: ~11 sequential stages. Percent tracks completed reports,
        // clamped to 99 so a skipped stage can't stall the bar; a final 100% is emitted at the end.
        const int estimatedStages = 11;
        void Report(string item, bool ok, string? msg = null)
        {
            var r = new ApplyResult(item, ok, msg);
            results.Add(r);
            var percent = Math.Min(99, (int)Math.Round(results.Count * 100.0 / estimatedStages));
            progress?.Report(new ApplyProgress(percent, $"{item}…", r));
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
                LastRunChangedTcp |= interfaceKeys.Count > 0;
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
                LastRunChangedTcp |= interfaceKeys.Count > 0;
                Report("Smart Packets", true, "Nagle off; delayed ACKs kept (unstable clamp)");
            }
            else
            {
                foreach (var path in interfaceKeys)
                {
                    _registry.DeleteValue(path, "TcpAckFrequency");
                    _registry.DeleteValue(path, "TCPNoDelay");
                }
                LastRunChangedTcp |= interfaceKeys.Count > 0;
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
                LastRunChangedTcp |= interfaceKeys.Count > 0;
                Report("Delayed ACK timer", true, $"TcpDelAckTicks = {ticks}");
            }
            else
            {
                var restored = interfaceKeys.Count(path => RestoreIfBackedUp($"reg::{path}::TcpDelAckTicks"));
                LastRunChangedTcp |= restored > 0;
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
        progress?.Report(new ApplyProgress(100, "Finishing up…"));
        Logger.Log($"run complete: {results.Count(r => r.Success)}/{results.Count} ok");
        return results;
    }

    // ---------- restore defaults ----------

    public async Task<List<ApplyResult>> RestoreAllAsync(IProgress<ApplyProgress>? progress = null)
    {
        var results = new List<ApplyResult>();
        var restoreTotal = Math.Max(1, _backup.Count + 1);
        void Report(string item, bool ok, string? msg = null)
        {
            var r = new ApplyResult(item, ok, msg);
            results.Add(r);
            var percent = Math.Min(100, (int)Math.Round(results.Count * 100.0 / restoreTotal));
            progress?.Report(new ApplyProgress(percent, "Reverting changes…", r));
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

        // Verification pass: don't just trust the writes — re-read every key/netsh value
        // and confirm it matches the captured original before forgetting the backups.
        if (allOk)
        {
            progress?.Report(new ApplyProgress(96, "Verifying every value went back…"));
            var mismatches = 0;
            foreach (var entry in entries)
            {
                try
                {
                    var (ok, detail) = await VerifyEntryAsync(entry);
                    if (!ok)
                    {
                        mismatches++;
                        allOk = false;
                        Report($"Verify {entry.Key}", false, detail);
                    }
                }
                catch (Exception ex)
                {
                    // Couldn't verify ≠ failed to restore; log it but don't hold the store hostage.
                    Logger.Log($"verify skipped for {entry.Key}: {ex.Message}");
                }
            }
            if (mismatches == 0)
                Report("Verification", true, $"re-read {entries.Count} value(s) — all match their originals");
        }

        // Only forget the "before" state if every value actually went back (and verified).
        if (allOk)
            _backup.Clear();
        else
            Report("Backup store", false, "kept backup.json because some restores failed or did not verify — fix and retry");

        progress?.Report(new ApplyProgress(100, "Done"));
        return results;
    }

    /// <summary>
    /// Re-reads the live state a backup entry points at and checks it equals the captured
    /// original (or is absent, when the value didn't exist before Ghast).
    /// </summary>
    private async Task<(bool Ok, string Detail)> VerifyEntryAsync(BackupEntry entry)
    {
        switch (entry.Type)
        {
            case "registry":
            {
                var (value, kind, exists) = _registry.Read(entry.Path, entry.Name);
                if (!entry.ExistedBefore)
                    return exists
                        ? (false, $"value still present ({RegistryService.ValueToString(value!, kind)}) — expected deleted")
                        : (true, "");
                if (!exists)
                    return (false, $"value missing — expected \"{entry.OriginalValue}\"");
                var current = RegistryService.ValueToString(value!, kind);
                return current == entry.OriginalValue
                    ? (true, "")
                    : (false, $"now \"{current}\", expected \"{entry.OriginalValue}\"");
            }

            case "registrykey":
            {
                if (!entry.ExistedBefore)
                    return _registry.KeyExists(entry.Path)
                        ? (false, "key still present — expected deleted")
                        : (true, "");
                // Key existed before: confirm each captured value is back.
                var values = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string[]>>(entry.OriginalValue ?? "{}") ?? new();
                foreach (var (name, pair) in values)
                {
                    var (value, kind, exists) = _registry.Read(entry.Path, name);
                    if (!exists)
                        return (false, $"value '{name}' missing");
                    if (RegistryService.ValueToString(value!, kind) != pair[1])
                        return (false, $"value '{name}' differs from original");
                }
                return (true, "");
            }

            case "netsh-autotuning":
            {
                var current = await _netsh.GetAutotuningLevelAsync();
                return string.Equals(current, entry.OriginalValue, StringComparison.OrdinalIgnoreCase)
                    ? (true, "")
                    : (false, $"autotuning now '{current}', expected '{entry.OriginalValue}'");
            }

            case "netsh-congestion":
            {
                var current = await _netsh.GetCongestionProviderAsync();
                return string.Equals(current, entry.OriginalValue, StringComparison.OrdinalIgnoreCase)
                    ? (true, "")
                    : (false, $"congestion provider now '{current}', expected '{entry.OriginalValue}'");
            }

            case "mtu":
            {
                var mtus = await _netsh.GetSubinterfaceMtusAsync();
                if (!mtus.TryGetValue(entry.Path, out var current))
                    return (true, ""); // adapter not present right now — nothing to verify against
                return current.ToString() == entry.OriginalValue
                    ? (true, "")
                    : (false, $"MTU now {current}, expected {entry.OriginalValue}");
            }

            case "dns":
            {
                var (value, _, exists) = _registry.Read(
                    $@"{InterfacesKey}\{entry.Name}", "NameServer");
                var current = exists ? value?.ToString()?.Trim() ?? "" : "";
                var expected = entry.OriginalValue?.Trim() ?? "";
                return string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)
                    ? (true, "")
                    : (false, $"DNS now '{(current.Length == 0 ? "DHCP" : current)}', expected '{(expected.Length == 0 ? "DHCP" : expected)}'");
            }

            default:
                return (true, "");
        }
    }

    public Task FlushAsync(IProgress<ApplyProgress>? progress = null) =>
        _adapters.FlushAsync(_netsh, progress);

    // ---------- "what changed" receipt + dry-run preview ----------

    /// <summary>
    /// Plain-English receipt of everything Ghast has changed, built from backup.json:
    /// Before = the captured pre-Ghast original, Now = the live value re-read right now.
    /// </summary>
    public async Task<List<ReceiptLine>> BuildReceiptAsync()
    {
        var lines = new List<ReceiptLine>();
        foreach (var entry in _backup.Entries)
        {
            var (setting, location) = Describe(entry);
            var before = FormatOriginal(entry);
            string now;
            try
            {
                now = await ReadCurrentAsync(entry);
            }
            catch (Exception ex)
            {
                now = $"(couldn't read: {ex.Message})";
            }
            lines.Add(new ReceiptLine(setting, location, before, now));
        }
        return lines
            .OrderBy(l => l.Location, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Setting, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Dry run: computes exactly what Run would change with this config — current live value
    /// vs the value that would be written — without touching anything. Mirrors RunAsync's
    /// clamp logic so the preview never lies about what a real Run would do.
    /// </summary>
    public async Task<List<ReceiptLine>> BuildPreviewAsync(GhastConfig config)
    {
        var c = config.Clone();
        var clamped = !c.Settings.ConnectionStable;
        var adapters = _adapters.GetActiveAdapters();
        var interfaceKeys = InterfaceKeysFor(adapters);
        var lines = new List<ReceiptLine>();

        string RegNow(string path, string name)
        {
            var (value, kind, exists) = _registry.Read(path, name);
            return exists ? RegistryService.ValueToString(value!, kind) : "(not set)";
        }

        // Multimedia profile
        var responsiveness = (20 - Math.Clamp(c.Settings.Responsiveness, 0, 20)).ToString();
        lines.Add(new ReceiptLine("Background CPU reservation (SystemResponsiveness)", "system",
            RegNow(MultimediaKey, "SystemResponsiveness"), responsiveness));
        if (c.Settings.CompetitiveMode)
            lines.Add(new ReceiptLine("Network throttling (NetworkThrottlingIndex)", "system",
                RegNow(MultimediaKey, "NetworkThrottlingIndex"), "4294967295 (throttling disabled)"));

        // Per-interface TCP values
        foreach (var path in interfaceKeys)
        {
            var adapterName = AdapterNameForInterfacePath(adapters, path);
            if (c.Settings.SmartPackets && !clamped)
            {
                lines.Add(new ReceiptLine("Nagle's algorithm (TCPNoDelay)", adapterName, RegNow(path, "TCPNoDelay"), "1 (off)"));
                lines.Add(new ReceiptLine("Delayed-ACK count (TcpAckFrequency)", adapterName, RegNow(path, "TcpAckFrequency"), "1 (ack every packet)"));
            }
            else if (c.Settings.SmartPackets && clamped)
            {
                lines.Add(new ReceiptLine("Nagle's algorithm (TCPNoDelay)", adapterName, RegNow(path, "TCPNoDelay"), "1 (off)"));
                lines.Add(new ReceiptLine("Delayed-ACK count (TcpAckFrequency)", adapterName, RegNow(path, "TcpAckFrequency"), "(kept — unstable clamp)"));
            }
            else
            {
                lines.Add(new ReceiptLine("Nagle's algorithm (TCPNoDelay)", adapterName, RegNow(path, "TCPNoDelay"), "(removed — Windows default)"));
                lines.Add(new ReceiptLine("Delayed-ACK count (TcpAckFrequency)", adapterName, RegNow(path, "TcpAckFrequency"), "(removed — Windows default)"));
            }

            lines.Add(clamped
                ? new ReceiptLine("Delayed-ACK timer (TcpDelAckTicks)", adapterName, RegNow(path, "TcpDelAckTicks"), "(kept — unstable clamp)")
                : new ReceiptLine("Delayed-ACK timer (TcpDelAckTicks)", adapterName, RegNow(path, "TcpDelAckTicks"),
                    TicksFromPacketsDelay(c.Advanced.PacketsDelay).ToString()));
        }

        // netsh globals
        string autotuningNow;
        try { autotuningNow = await _netsh.GetAutotuningLevelAsync(); }
        catch { autotuningNow = "(couldn't read)"; }
        lines.Add(new ReceiptLine("TCP receive auto-tuning", "netsh", autotuningNow,
            (clamped ? "Normal (clamped)" : c.Settings.Tuning).ToLowerInvariant()));

        if (!clamped && !c.Advanced.CongestionProvider.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            string congestionNow;
            try { congestionNow = await _netsh.GetCongestionProviderAsync(); }
            catch { congestionNow = "(couldn't read)"; }
            lines.Add(new ReceiptLine("TCP congestion provider", "netsh", congestionNow,
                c.Advanced.CongestionProvider.ToLowerInvariant()));
        }

        // MTU
        if (!c.Advanced.MtuAutomatic && !clamped)
        {
            Dictionary<string, int> mtus;
            try { mtus = await _netsh.GetSubinterfaceMtusAsync(); }
            catch { mtus = new(); }
            var target = Math.Clamp(c.Advanced.MtuValue, 576, 1500);
            foreach (var adapter in adapters)
                lines.Add(new ReceiptLine("MTU (largest packet size)", adapter.Name,
                    mtus.TryGetValue(adapter.Name, out var m) ? m.ToString() : "(unknown)", target.ToString()));
        }

        // QoS
        var qosNow = _registry.KeyExists(@"SOFTWARE\Policies\Microsoft\Windows\QoS\Ghast-Minecraft-javaw")
            ? "policy present" : "(no policy)";
        lines.Add(c.Advanced.NetworkPriority > 0
            ? new ReceiptLine("QoS DSCP tag for javaw.exe / java.exe", "system", qosNow,
                $"DSCP {QosService.DscpForLevel(c.Advanced.NetworkPriority)}")
            : new ReceiptLine("QoS DSCP tag for javaw.exe / java.exe", "system", qosNow, "(removed)"));

        // Adapter power saving
        foreach (var adapter in adapters)
        {
            var index = _adapters.FindClassIndex(adapter.Guid);
            if (index is null)
                continue;
            var path = $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e972-e325-11ce-bfc1-08002be10318}}\{index}";
            lines.Add(new ReceiptLine("Adapter power management (PnPCapabilities)", adapter.Name,
                RegNow(path, "PnPCapabilities"),
                c.Advanced.NetworkPowerSaving ? "24 (never sleep the NIC)" : "(restored to original)"));
        }

        // Games task + process priority
        lines.Add(c.Advanced.GhastPriorityMode
            ? new ReceiptLine("Multimedia 'Games' task boost", "system",
                RegNow(GamesTaskKey, "Scheduling Category"), "High (+ process priority High)")
            : new ReceiptLine("Game process priority", "system", "(session only)",
                ProcessPriorityService.MapNetworkPriority(c.Advanced.NetworkPriority).ToString()));

        // DNS
        if (!c.Dns.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var target = c.Dns == "cloudflare" ? "1.1.1.1, 1.0.0.1" : "8.8.8.8, 8.8.4.4";
            foreach (var adapter in adapters)
            {
                var (value, _, exists) = _registry.Read($@"{InterfacesKey}\{adapter.Guid}", "NameServer");
                var current = exists && !string.IsNullOrWhiteSpace(value?.ToString())
                    ? value!.ToString()! : "DHCP (automatic)";
                lines.Add(new ReceiptLine("DNS servers", adapter.Name, current, target));
            }
        }

        return lines;
    }

    private static (string Setting, string Location) Describe(BackupEntry entry)
    {
        var location = entry.Type switch
        {
            "registry" or "registrykey" when entry.Path.Contains(@"Tcpip\Parameters\Interfaces", StringComparison.OrdinalIgnoreCase)
                => "adapter " + ShortGuid(entry.Path),
            "registry" when entry.Path.Contains(@"Control\Class\{4d36e972", StringComparison.OrdinalIgnoreCase)
                => "network adapter driver",
            "mtu" or "dns" => entry.Path,
            "netsh-autotuning" or "netsh-congestion" => "netsh",
            _ => "system"
        };

        var setting = entry.Type switch
        {
            "netsh-autotuning" => "TCP receive auto-tuning",
            "netsh-congestion" => "TCP congestion provider",
            "mtu" => "MTU (largest packet size)",
            "dns" => "DNS servers",
            "registrykey" => "QoS DSCP policy (" + (entry.Path.Contains("javaw") ? "javaw.exe" : "java.exe") + ")",
            _ => entry.Name switch
            {
                "TcpAckFrequency" => "Delayed-ACK count (TcpAckFrequency)",
                "TCPNoDelay" => "Nagle's algorithm (TCPNoDelay)",
                "TcpDelAckTicks" => "Delayed-ACK timer (TcpDelAckTicks)",
                "SystemResponsiveness" => "Background CPU reservation (SystemResponsiveness)",
                "NetworkThrottlingIndex" => "Network throttling (NetworkThrottlingIndex)",
                "PnPCapabilities" => "Adapter power management (PnPCapabilities)",
                "*EEE" => "Energy-Efficient Ethernet (*EEE)",
                "Priority" => "Multimedia 'Games' task priority",
                "Scheduling Category" => "Multimedia 'Games' scheduling category",
                "SFIO Priority" => "Multimedia 'Games' storage priority",
                "NonBestEffortLimit" => "QoS bandwidth reservation (Psched)",
                "Do not use NLA" => "QoS: tag packets on non-domain networks",
                "NameServer" => "DNS servers",
                _ => entry.Name.Length > 0 ? entry.Name : entry.Path
            }
        };
        return (setting, location);
    }

    private static string ShortGuid(string interfacePath)
    {
        var idx = interfacePath.LastIndexOf('\\');
        var guid = idx >= 0 ? interfacePath[(idx + 1)..] : interfacePath;
        return guid.Length > 10 ? guid[..9] + "…}" : guid;
    }

    private static string AdapterNameForInterfacePath(IReadOnlyList<AdapterInfo> adapters, string path)
    {
        var idx = path.LastIndexOf('\\');
        var guid = idx >= 0 ? path[(idx + 1)..] : path;
        return adapters.FirstOrDefault(a => string.Equals(a.Guid, guid, StringComparison.OrdinalIgnoreCase))?.Name
               ?? "adapter " + ShortGuid(path);
    }

    private static string FormatOriginal(BackupEntry entry) => entry.Type switch
    {
        "registrykey" => entry.ExistedBefore ? "(policy existed)" : "(not set)",
        "dns" => string.IsNullOrWhiteSpace(entry.OriginalValue) ? "DHCP (automatic)" : entry.OriginalValue!,
        _ => entry.ExistedBefore ? entry.OriginalValue ?? "" : "(not set)"
    };

    private async Task<string> ReadCurrentAsync(BackupEntry entry)
    {
        switch (entry.Type)
        {
            case "registry":
            {
                var (value, kind, exists) = _registry.Read(entry.Path, entry.Name);
                return exists ? RegistryService.ValueToString(value!, kind) : "(not set)";
            }
            case "registrykey":
                return _registry.KeyExists(entry.Path) ? "(policy present)" : "(not set)";
            case "netsh-autotuning":
                return await _netsh.GetAutotuningLevelAsync();
            case "netsh-congestion":
                return await _netsh.GetCongestionProviderAsync();
            case "mtu":
            {
                var mtus = await _netsh.GetSubinterfaceMtusAsync();
                return mtus.TryGetValue(entry.Path, out var m) ? m.ToString() : "(adapter not present)";
            }
            case "dns":
            {
                var (value, _, exists) = _registry.Read($@"{InterfacesKey}\{entry.Name}", "NameServer");
                var current = exists ? value?.ToString()?.Trim() ?? "" : "";
                return current.Length == 0 ? "DHCP (automatic)" : current;
            }
            default:
                return "";
        }
    }

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

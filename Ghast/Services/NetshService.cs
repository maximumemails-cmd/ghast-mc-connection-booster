using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ghast.Models;

namespace Ghast.Services;

/// <summary>
/// Runs netsh with captured output, and owns the netsh-backed tweaks:
/// TCP autotuning, congestion provider, MTU, and DNS. Each writes a backup entry first.
/// </summary>
public class NetshService
{
    private readonly BackupService _backup;
    private readonly RegistryService _registry;

    public NetshService(BackupService backup, RegistryService registry)
    {
        _backup = backup;
        _registry = registry;
    }

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public async Task<(int ExitCode, string Output)> RunAsync(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("Failed to start netsh");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        // Bounded wait: a wedged netsh must never hang an apply/revert pass forever.
        using var cts = new CancellationTokenSource(CommandTimeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Logger.Log($"netsh {args} -> TIMED OUT after {CommandTimeout.TotalSeconds}s (killed)");
            return (-1, $"netsh timed out after {CommandTimeout.TotalSeconds} seconds");
        }

        var output = (await stdoutTask + await stderrTask).Trim();
        Logger.Log($"netsh {args} -> exit {proc.ExitCode}{(proc.ExitCode != 0 ? $": {output}" : "")}");
        return (proc.ExitCode, output);
    }

    private static string? ParseValueAfterColon(string output, string lineContains)
    {
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains(lineContains, StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                return line[(line.IndexOf(':') + 1)..].Trim();
        }
        return null;
    }

    // ---------- autotuning ----------

    public async Task<string> GetAutotuningLevelAsync()
    {
        var (_, output) = await RunAsync("int tcp show global");
        // English Windows prints "Receive Window Auto-Tuning Level : normal".
        // On other locales the labeled line may not be found; we then assume the
        // Windows default and log it, because this value seeds the immutable backup.
        var raw = ParseValueAfterColon(output, "Auto-Tuning")?.ToLowerInvariant();
        // "highlyrestricted" must be tested before "restricted" (substring overlap).
        string[] known = { "disabled", "highlyrestricted", "restricted", "experimental", "normal" };
        var match = raw is null ? null : known.FirstOrDefault(k => raw.Contains(k));
        if (match is null)
        {
            Logger.Log($"WARN: could not parse autotuning level from netsh output " +
                       $"(localized Windows?); assuming 'normal'. Raw: '{raw}'");
            return "normal";
        }
        return match;
    }

    public async Task SetAutotuningAsync(string level)
    {
        const string key = "netsh::autotuning";
        if (!_backup.Has(key))
        {
            var original = await GetAutotuningLevelAsync();
            _backup.Record(new BackupEntry
            {
                Key = key, Type = "netsh-autotuning", Path = "tcp global",
                Name = "autotuninglevel", OriginalValue = original, ExistedBefore = true
            });
        }

        var (code, output) = await RunAsync($"int tcp set global autotuninglevel={level.ToLowerInvariant()}");
        if (code != 0)
            throw new InvalidOperationException($"netsh autotuning failed: {output}");
    }

    // ---------- congestion provider ----------

    /// <summary>Windows 10 1709+ uses the supplemental template; older builds use the global switch.</summary>
    private static bool UseSupplemental => Environment.OSVersion.Version.Build >= 16299;

    public async Task<string> GetCongestionProviderAsync()
    {
        // "dctcp" must be tested before "ctcp" (substring overlap would corrupt the backup).
        if (UseSupplemental)
        {
            var (_, output) = await RunAsync("int tcp show supplemental template=internet");
            var raw = ParseValueAfterColon(output, "Congestion")?.ToLowerInvariant();
            string[] known = { "dctcp", "cubic", "newreno", "bbr2", "ctcp", "none" };
            var match = raw is null ? null : known.FirstOrDefault(k => raw.Contains(k));
            if (match is null)
            {
                Logger.Log($"WARN: could not parse congestion provider from netsh output " +
                           $"(localized Windows?); assuming 'cubic'. Raw: '{raw}'");
                return "cubic";
            }
            return match;
        }
        else
        {
            var (_, output) = await RunAsync("int tcp show global");
            var raw = ParseValueAfterColon(output, "Congestion")?.ToLowerInvariant();
            string[] known = { "dctcp", "ctcp", "none" };
            var match = raw is null ? null : known.FirstOrDefault(k => raw.Contains(k));
            if (match is null)
            {
                Logger.Log($"WARN: could not parse congestion provider from netsh output " +
                           $"(localized Windows?); assuming 'none'. Raw: '{raw}'");
                return "none";
            }
            return match;
        }
    }

    /// <summary>
    /// provider is one of the UI options: Default|CUBIC|CTCP|NewReno|DCTCP.
    /// "Default" restores the original provider if Ghast changed it before, otherwise does nothing.
    /// </summary>
    public async Task<string> ApplyCongestionProviderAsync(string provider)
    {
        const string key = "netsh::congestion";

        if (provider.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            var entry = _backup.Get(key);
            if (entry?.OriginalValue is { Length: > 0 } original)
            {
                await SetCongestionRawAsync(original);
                return $"reset to original ({original})";
            }
            return "left at Windows default";
        }

        if (!_backup.Has(key))
        {
            var original = await GetCongestionProviderAsync();
            _backup.Record(new BackupEntry
            {
                Key = key, Type = "netsh-congestion", Path = "tcp supplemental",
                Name = "congestionprovider", OriginalValue = original, ExistedBefore = true
            });
        }

        await SetCongestionRawAsync(provider.ToLowerInvariant());
        return $"set to {provider}";
    }

    public async Task SetCongestionRawAsync(string provider)
    {
        provider = provider.ToLowerInvariant();
        (int code, string output) result;
        if (UseSupplemental)
        {
            result = await RunAsync($"int tcp set supplemental template=internet congestionprovider={provider}");
        }
        else
        {
            if (provider is not ("ctcp" or "dctcp" or "none" or "default"))
                throw new InvalidOperationException($"'{provider}' is not supported on this Windows build (only CTCP/DCTCP).");
            result = await RunAsync($"int tcp set global congestionprovider={provider}");
        }
        if (result.code != 0)
            throw new InvalidOperationException($"netsh congestion provider failed: {result.output}");
    }

    // ---------- MTU ----------

    /// <summary>Parses "netsh interface ipv4 show subinterfaces": MTU is column 1, interface name is the tail.</summary>
    public async Task<Dictionary<string, int>> GetSubinterfaceMtusAsync()
    {
        var (_, output) = await RunAsync("interface ipv4 show subinterfaces");
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^(\d+)\s+\S+\s+\S+\s+\S+\s+(.+)$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var mtu))
                result[m.Groups[2].Value.Trim()] = mtu;
        }
        return result;
    }

    public async Task SetMtuAsync(string adapterName, int mtu)
    {
        var key = $"netsh::mtu::{adapterName}";
        if (!_backup.Has(key))
        {
            var current = await GetSubinterfaceMtusAsync();
            if (!current.TryGetValue(adapterName, out var original))
                throw new InvalidOperationException($"Could not read the current MTU of '{adapterName}'; not changing it.");
            _backup.Record(new BackupEntry
            {
                Key = key, Type = "mtu", Path = adapterName,
                Name = "mtu", OriginalValue = original.ToString(), ExistedBefore = true
            });
        }

        var (code, output) = await RunAsync(
            $"interface ipv4 set subinterface \"{adapterName}\" mtu={mtu} store=persistent");
        if (code != 0)
            throw new InvalidOperationException($"netsh set mtu failed: {output}");
    }

    // ---------- DNS ----------

    private const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    /// <summary>choice: none|cloudflare|google. "none" = leave the current DNS alone.</summary>
    public async Task<string> ApplyDnsAsync(string choice, IReadOnlyList<AdapterInfo> adapters)
    {
        var (primary, secondary) = choice.ToLowerInvariant() switch
        {
            "cloudflare" => ("1.1.1.1", "1.0.0.1"),
            "google" => ("8.8.8.8", "8.8.4.4"),
            _ => (null, null)
        };
        if (primary is null)
            return "left alone";

        foreach (var adapter in adapters)
        {
            BackupDns(adapter);

            var (code, output) = await RunAsync(
                $"interface ip set dns name=\"{adapter.Name}\" static {primary} primary");
            if (code != 0)
                throw new InvalidOperationException($"set dns on '{adapter.Name}' failed: {output}");

            await RunAsync($"interface ip add dns name=\"{adapter.Name}\" {secondary} index=2");
        }
        return $"{primary} / {secondary} on {adapters.Count} adapter(s)";
    }

    private void BackupDns(AdapterInfo adapter)
    {
        var key = $"dns::{adapter.Guid}";
        if (_backup.Has(key))
            return;

        // A non-empty NameServer value means static DNS was configured; empty/absent means DHCP.
        var (value, _, exists) = _registry.Read($@"{InterfacesKey}\{adapter.Guid}", "NameServer");
        var original = exists ? value?.ToString() ?? "" : "";
        _backup.Record(new BackupEntry
        {
            Key = key, Type = "dns", Path = adapter.Name, Name = adapter.Guid,
            OriginalValue = original, ExistedBefore = true
        });
    }

    /// <summary>Throws on any failed command so RestoreAll never clears a backup it didn't apply.</summary>
    public async Task RestoreDnsAsync(BackupEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.OriginalValue))
        {
            var (code, output) = await RunAsync($"interface ip set dns name=\"{entry.Path}\" dhcp");
            if (code != 0)
                throw new InvalidOperationException($"restoring DHCP DNS on '{entry.Path}' failed: {output}");
            return;
        }

        var servers = entry.OriginalValue.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < servers.Length; i++)
        {
            var (code, output) = i == 0
                ? await RunAsync($"interface ip set dns name=\"{entry.Path}\" static {servers[i]} primary")
                : await RunAsync($"interface ip add dns name=\"{entry.Path}\" {servers[i]} index={i + 1}");
            if (code != 0)
                throw new InvalidOperationException($"restoring DNS '{servers[i]}' on '{entry.Path}' failed: {output}");
        }
    }
}

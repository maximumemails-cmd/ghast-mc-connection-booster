using System.Net.NetworkInformation;
using Ghast.Models;
using Microsoft.Win32;

namespace Ghast.Services;

public record AdapterInfo(string Name, string Description, string Guid);

/// <summary>
/// Enumerates active NICs and manages the per-adapter power settings that live under
/// the network class key. Also owns the "flush" helper (flushdns + adapter bounce).
/// </summary>
public class AdapterService
{
    private const string NetClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    private readonly RegistryService _registry;
    private readonly BackupService _backup;

    public AdapterService(RegistryService registry, BackupService backup)
    {
        _registry = registry;
        _backup = backup;
    }

    public IReadOnlyList<AdapterInfo> GetActiveAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(n => new AdapterInfo(n.Name, n.Description, n.Id))
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Error("enumerating adapters", ex);
            return Array.Empty<AdapterInfo>();
        }
    }

    /// <summary>Finds the four-digit class subkey (e.g. "0001") whose NetCfgInstanceId matches the adapter GUID.</summary>
    public string? FindClassIndex(string adapterGuid)
    {
        foreach (var index in _registry.GetSubKeyNames(NetClassKey))
        {
            if (index.Length != 4 || !index.All(char.IsDigit))
                continue;
            try
            {
                var (value, _, exists) = _registry.Read($@"{NetClassKey}\{index}", "NetCfgInstanceId");
                if (exists && string.Equals(value?.ToString(), adapterGuid, StringComparison.OrdinalIgnoreCase))
                    return index;
            }
            catch
            {
                // Some class subkeys are ACL'd even for admins; skip them.
            }
        }
        return null;
    }

    /// <summary>
    /// disable=true writes PnPCapabilities=24 (device cannot be turned off to save power)
    /// and *EEE=0 where the driver exposes it. disable=false restores the backed-up state.
    /// </summary>
    public void SetPowerSaving(AdapterInfo adapter, bool disablePowerSaving)
    {
        var index = FindClassIndex(adapter.Guid)
                    ?? throw new InvalidOperationException($"No driver class key found for '{adapter.Name}'");
        var path = $@"{NetClassKey}\{index}";

        if (disablePowerSaving)
        {
            _registry.SetDword(path, "PnPCapabilities", 24);

            // Drivers declare *EEE with different value kinds — write back the same kind
            // so the driver keeps parsing it (most use REG_SZ, some use REG_DWORD).
            var (_, eeeKind, eeeExists) = _registry.Read(path, "*EEE");
            if (eeeExists)
            {
                if (eeeKind == Microsoft.Win32.RegistryValueKind.DWord)
                    _registry.SetDword(path, "*EEE", 0);
                else
                    _registry.SetString(path, "*EEE", "0");
            }
        }
        else
        {
            RestoreIfBackedUp($"reg::{path}::PnPCapabilities");
            RestoreIfBackedUp($"reg::{path}::*EEE");
        }
    }

    private void RestoreIfBackedUp(string key)
    {
        var entry = _backup.Get(key);
        if (entry is not null)
            _registry.RestoreEntry(entry);
    }

    /// <summary>
    /// Light-weight stack refresh: flush the DNS cache and bounce each active adapter.
    /// Briefly drops the connection — the caller must warn the user first.
    /// </summary>
    public async Task FlushAsync(NetshService netsh)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ipconfig", "/flushdns")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
                await proc.WaitForExitAsync();
            Logger.Log("ipconfig /flushdns done");
        }
        catch (Exception ex)
        {
            Logger.Error("flushdns", ex);
        }

        foreach (var adapter in GetActiveAdapters())
        {
            await netsh.RunAsync($"interface set interface name=\"{adapter.Name}\" admin=disable");
            await Task.Delay(1500);
            await netsh.RunAsync($"interface set interface name=\"{adapter.Name}\" admin=enable");
        }
    }
}

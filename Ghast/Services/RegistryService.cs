using System.Text.Json;
using Ghast.Models;
using Microsoft.Win32;

namespace Ghast.Services;

/// <summary>
/// Safe HKLM read/write. Every mutating call records a BackupEntry first (spec's golden rule).
/// Uses the 64-bit registry view so writes are not redirected under WOW64.
/// </summary>
public class RegistryService
{
    private readonly BackupService _backup;

    public RegistryService(BackupService backup) => _backup = backup;

    private static RegistryKey OpenBase() =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);

    // ---------- reads ----------

    public (object? Value, RegistryValueKind Kind, bool Exists) Read(string path, string name)
    {
        using var root = OpenBase();
        using var key = root.OpenSubKey(path);
        if (key is null)
            return (null, RegistryValueKind.Unknown, false);
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
            return (null, RegistryValueKind.Unknown, false);
        return (value, key.GetValueKind(name), true);
    }

    public bool KeyExists(string path)
    {
        using var root = OpenBase();
        using var key = root.OpenSubKey(path);
        return key is not null;
    }

    public string[] GetSubKeyNames(string path)
    {
        using var root = OpenBase();
        using var key = root.OpenSubKey(path);
        return key?.GetSubKeyNames() ?? Array.Empty<string>();
    }

    // ---------- backed-up writes ----------

    public void SetDword(string path, string name, uint value)
    {
        BackupValue(path, name);
        SetDwordNoBackup(path, name, value);
    }

    public void SetString(string path, string name, string value)
    {
        BackupValue(path, name);
        SetStringNoBackup(path, name, value);
    }

    /// <summary>Backs up (or records "absent") and then deletes the value if present.</summary>
    public void DeleteValue(string path, string name)
    {
        BackupValue(path, name);
        DeleteValueNoBackup(path, name);
    }

    // ---------- raw writes (used after a whole-subkey backup has been captured) ----------

    public void SetDwordNoBackup(string path, string name, uint value)
    {
        using var root = OpenBase();
        using var key = root.CreateSubKey(path, writable: true)
                        ?? throw new InvalidOperationException($"Cannot open HKLM\\{path}");
        key.SetValue(name, unchecked((int)value), RegistryValueKind.DWord);
        Logger.Log($"reg write HKLM\\{path}\\{name} = dword:{value:x8}");
    }

    public void SetStringNoBackup(string path, string name, string value)
    {
        using var root = OpenBase();
        using var key = root.CreateSubKey(path, writable: true)
                        ?? throw new InvalidOperationException($"Cannot open HKLM\\{path}");
        key.SetValue(name, value, RegistryValueKind.String);
        Logger.Log($"reg write HKLM\\{path}\\{name} = \"{value}\"");
    }

    public void DeleteValueNoBackup(string path, string name)
    {
        using var root = OpenBase();
        using var key = root.OpenSubKey(path, writable: true);
        if (key?.GetValue(name) is not null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            Logger.Log($"reg delete HKLM\\{path}\\{name}");
        }
    }

    // ---------- whole-subkey handling (QoS policies) ----------

    /// <summary>
    /// Captures every value directly under the key as a JSON map, or "absent" if the key
    /// does not exist. Restore recreates the values, or deletes the whole tree.
    /// </summary>
    public void BackupSubKey(string path)
    {
        var backupKey = $"regkey::{path}";
        if (_backup.Has(backupKey))
            return;

        using var root = OpenBase();
        using var key = root.OpenSubKey(path);
        if (key is null)
        {
            _backup.Record(new BackupEntry
            {
                Key = backupKey, Type = "registrykey", Path = path, ExistedBefore = false
            });
            return;
        }

        var values = new Dictionary<string, string[]>();
        foreach (var name in key.GetValueNames())
        {
            var v = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (v is null) continue;
            var kind = key.GetValueKind(name);
            values[name] = new[] { kind.ToString(), ValueToString(v, kind) };
        }

        _backup.Record(new BackupEntry
        {
            Key = backupKey,
            Type = "registrykey",
            Path = path,
            ExistedBefore = true,
            OriginalValue = JsonSerializer.Serialize(values)
        });
    }

    public void DeleteSubKeyTree(string path)
    {
        BackupSubKey(path);
        using var root = OpenBase();
        if (root.OpenSubKey(path) is not null)
        {
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            Logger.Log($"reg delete tree HKLM\\{path}");
        }
    }

    public void DeleteSubKeyTreeNoBackup(string path)
    {
        using var root = OpenBase();
        if (root.OpenSubKey(path) is not null)
        {
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            Logger.Log($"reg delete tree HKLM\\{path}");
        }
    }

    // ---------- restore ----------

    public void RestoreEntry(BackupEntry entry)
    {
        switch (entry.Type)
        {
            case "registry":
                if (!entry.ExistedBefore)
                {
                    DeleteValueNoBackup(entry.Path, entry.Name);
                    // If Ghast created the parent key too, remove it once it is empty again.
                    if (entry.KeyExistedBefore == false)
                        DeleteKeyIfEmpty(entry.Path);
                }
                else
                {
                    var kind = Enum.TryParse<RegistryValueKind>(entry.ValueKind, out var k) ? k : RegistryValueKind.String;
                    WriteRaw(entry.Path, entry.Name, kind, entry.OriginalValue ?? "");
                }
                break;

            case "registrykey":
                if (!entry.ExistedBefore)
                {
                    DeleteSubKeyTreeNoBackup(entry.Path);
                }
                else
                {
                    var values = JsonSerializer.Deserialize<Dictionary<string, string[]>>(entry.OriginalValue ?? "{}")
                                 ?? new();
                    foreach (var (name, pair) in values)
                    {
                        var kind = Enum.TryParse<RegistryValueKind>(pair[0], out var k) ? k : RegistryValueKind.String;
                        WriteRaw(entry.Path, name, kind, pair[1]);
                    }
                }
                break;

            default:
                throw new InvalidOperationException($"RegistryService cannot restore entry type '{entry.Type}'");
        }
        Logger.Log($"restored {entry.Key}");
    }

    private void DeleteKeyIfEmpty(string path)
    {
        using var root = OpenBase();
        bool empty;
        using (var key = root.OpenSubKey(path))
        {
            if (key is null)
                return;
            empty = key.ValueCount == 0 && key.SubKeyCount == 0;
        }
        if (empty)
        {
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
            Logger.Log($"reg delete empty key HKLM\\{path} (created by Ghast)");
        }
    }

    private void WriteRaw(string path, string name, RegistryValueKind kind, string serialized)
    {
        using var root = OpenBase();
        using var key = root.CreateSubKey(path, writable: true)
                        ?? throw new InvalidOperationException($"Cannot open HKLM\\{path}");
        object value = kind switch
        {
            RegistryValueKind.DWord => unchecked((int)uint.Parse(serialized)),
            RegistryValueKind.QWord => unchecked((long)ulong.Parse(serialized)),
            RegistryValueKind.MultiString => serialized.Split('\n'),
            RegistryValueKind.Binary => Convert.FromBase64String(serialized),
            _ => serialized
        };
        key.SetValue(name, value, kind == RegistryValueKind.Unknown ? RegistryValueKind.String : kind);
    }

    // ---------- helpers ----------

    private void BackupValue(string path, string name)
    {
        var backupKey = $"reg::{path}::{name}";
        if (_backup.Has(backupKey))
            return;

        var (value, kind, exists) = Read(path, name);
        _backup.Record(new BackupEntry
        {
            Key = backupKey,
            Type = "registry",
            Path = path,
            Name = name,
            ExistedBefore = exists,
            KeyExistedBefore = KeyExists(path),
            ValueKind = exists ? kind.ToString() : null,
            OriginalValue = exists ? ValueToString(value!, kind) : null
        });
    }

    public static string ValueToString(object value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => Convert.ToUInt32(unchecked((uint)(int)value)).ToString(),
        RegistryValueKind.QWord => Convert.ToUInt64(unchecked((ulong)(long)value)).ToString(),
        RegistryValueKind.MultiString => string.Join('\n', (string[])value),
        RegistryValueKind.Binary => Convert.ToBase64String((byte[])value),
        _ => value.ToString() ?? ""
    };
}

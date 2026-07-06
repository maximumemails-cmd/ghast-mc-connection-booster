using System.IO;
using System.Text.Json;
using Ghast.Models;

namespace Ghast.Services;

/// <summary>
/// backup.json store. The first value captured for a key is the machine's true "before"
/// state and is never overwritten (spec §5.2). Restore-all is orchestrated by ApplyService,
/// which dispatches each entry back to the service that knows how to undo it.
/// </summary>
public class BackupService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _gate = new();
    private Dictionary<string, BackupEntry> _entries = new();

    public BackupService()
    {
        Load();
    }

    public bool Has(string key)
    {
        lock (_gate) return _entries.ContainsKey(key);
    }

    public BackupEntry? Get(string key)
    {
        lock (_gate) return _entries.TryGetValue(key, out var e) ? e : null;
    }

    public IReadOnlyList<BackupEntry> Entries
    {
        get { lock (_gate) return _entries.Values.ToList(); }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Records the entry only if no backup exists for this key yet.</summary>
    public void Record(BackupEntry entry)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(entry.Key))
                return;
            _entries[entry.Key] = entry;
            Save();
        }
        Logger.Log($"backup captured: {entry.Key} (existedBefore={entry.ExistedBefore})");
    }

    /// <summary>Called after a successful full restore: the machine is back to its pre-Ghast state.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Save();
        }
        Logger.Log("backup store cleared after restore");
    }

    private void Load()
    {
        try
        {
            if (File.Exists(Paths.BackupPath))
            {
                var list = JsonSerializer.Deserialize<List<BackupEntry>>(File.ReadAllText(Paths.BackupPath));
                _entries = (list ?? new()).Where(e => !string.IsNullOrEmpty(e.Key))
                                          .ToDictionary(e => e.Key, e => e);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("loading backup.json", ex);
            _entries = new();
        }
    }

    private void Save()
    {
        try
        {
            Paths.EnsureCreated();
            File.WriteAllText(Paths.BackupPath, JsonSerializer.Serialize(_entries.Values.ToList(), JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Error("saving backup.json", ex);
        }
    }
}

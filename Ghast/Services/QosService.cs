namespace Ghast.Services;

/// <summary>
/// QoS DSCP policy for the Minecraft executables (spec §4-QoS). Windows policy-based QoS
/// matches on a single Application Name per policy, so one policy is created per executable.
/// </summary>
public class QosService
{
    private const string QosPolicyRoot = @"SOFTWARE\Policies\Microsoft\Windows\QoS";
    private const string PschedKey = @"SOFTWARE\Policies\Microsoft\Windows\Psched";
    private const string TcpipQosKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\QoS";

    private static readonly (string PolicyName, string Executable)[] Policies =
    {
        ("Ghast-Minecraft-javaw", "javaw.exe"),
        ("Ghast-Minecraft-java", "java.exe")
    };

    private readonly RegistryService _registry;
    private readonly BackupService _backup;

    public QosService(RegistryService registry, BackupService backup)
    {
        _registry = registry;
        _backup = backup;
    }

    /// <summary>DSCP ladder: AF11, AF21, AF31, AF41, EF. Level 0 removes the policy.</summary>
    public static int DscpForLevel(int level) => level switch
    {
        <= 0 => 0,
        1 => 10,
        2 => 18,
        3 => 26,
        4 => 34,
        _ => 46
    };

    public string Apply(int level)
    {
        if (level <= 0)
        {
            Remove();
            return "policy removed";
        }

        var dscp = DscpForLevel(level);
        foreach (var (policyName, executable) in Policies)
        {
            var path = $@"{QosPolicyRoot}\{policyName}";
            _registry.BackupSubKey(path);

            _registry.SetStringNoBackup(path, "Version", "1.0");
            _registry.SetStringNoBackup(path, "Application Name", executable);
            _registry.SetStringNoBackup(path, "Protocol", "*");
            _registry.SetStringNoBackup(path, "Local Port", "*");
            _registry.SetStringNoBackup(path, "Local IP", "*");
            _registry.SetStringNoBackup(path, "Local IP Prefix Length", "*");
            _registry.SetStringNoBackup(path, "Remote Port", "*");
            _registry.SetStringNoBackup(path, "Remote IP", "*");
            _registry.SetStringNoBackup(path, "Remote IP Prefix Length", "*");
            _registry.SetStringNoBackup(path, "DSCP Value", dscp.ToString());
            _registry.SetStringNoBackup(path, "Throttle Rate", "-1");
        }

        // Without these two, Windows ignores the DSCP tag on non-domain machines.
        _registry.SetDword(PschedKey, "NonBestEffortLimit", 0);
        _registry.SetString(TcpipQosKey, "Do not use NLA", "1");

        return $"DSCP {dscp} for javaw.exe / java.exe";
    }

    public void Remove()
    {
        foreach (var (policyName, _) in Policies)
            _registry.DeleteSubKeyTree($@"{QosPolicyRoot}\{policyName}");

        RestoreIfBackedUp($"reg::{PschedKey}::NonBestEffortLimit");
        RestoreIfBackedUp($@"reg::{TcpipQosKey}::Do not use NLA");
    }

    private void RestoreIfBackedUp(string key)
    {
        var entry = _backup.Get(key);
        if (entry is not null)
            _registry.RestoreEntry(entry);
    }
}

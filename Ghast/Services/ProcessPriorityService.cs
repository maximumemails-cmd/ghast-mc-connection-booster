using System.Diagnostics;

namespace Ghast.Services;

/// <summary>
/// Raises/normalizes the priority of running Minecraft processes.
/// Process priorities are per-session, so nothing is persisted to backup.json;
/// RealTime is deliberately never offered (spec §5.5) — the cap is High.
/// </summary>
public class ProcessPriorityService
{
    private static readonly string[] ProcessNames = { "javaw", "java" };

    public IReadOnlyList<Process> FindMinecraftProcesses()
    {
        var found = new List<Process>();
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                var name = proc.ProcessName;
                if (ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || name.StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(proc);
                }
                else
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("enumerating processes", ex);
        }
        return found;
    }

    /// <summary>Returns how many processes were adjusted.</summary>
    public int SetPriority(ProcessPriorityClass priority)
    {
        if (priority == ProcessPriorityClass.RealTime)
            priority = ProcessPriorityClass.High; // hard cap, never RealTime

        var count = 0;
        foreach (var proc in FindMinecraftProcesses())
        {
            try
            {
                proc.PriorityClass = priority;
                count++;
                Logger.Log($"process {proc.ProcessName} (pid {proc.Id}) priority -> {priority}");
            }
            catch (Exception ex)
            {
                Logger.Error($"setting priority on {proc.ProcessName}", ex);
            }
            finally
            {
                proc.Dispose();
            }
        }
        return count;
    }

    public static ProcessPriorityClass MapNetworkPriority(int slider) => slider switch
    {
        <= 2 => ProcessPriorityClass.Normal,
        <= 4 => ProcessPriorityClass.AboveNormal,
        _ => ProcessPriorityClass.High
    };
}

using Microsoft.Win32;

namespace Ghast.Services;

/// <summary>
/// "Start Ghast when Windows starts" via HKCU\...\Run (per-user, no scheduled task).
/// Honest caveat surfaced in the UI: because Ghast's manifest demands administrator,
/// some Windows builds skip elevated apps in the Run key at logon instead of prompting.
/// The key itself is always written/removed correctly and is fully reversible.
/// </summary>
public class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Ghast";

    public bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex)
        {
            Logger.Error("reading startup Run key", ex);
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Startup entries only work on Windows.");

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                        ?? throw new InvalidOperationException("Couldn't open the startup registry key.");
        if (enabled)
        {
            var exe = Environment.ProcessPath
                      ?? throw new InvalidOperationException("Couldn't determine Ghast's own path.");
            key.SetValue(ValueName, $"\"{exe}\"");
            Logger.Log($"startup enabled -> {exe}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            Logger.Log("startup disabled");
        }
    }
}

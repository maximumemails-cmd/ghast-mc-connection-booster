using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Ghast.Services;

/// <summary>
/// Best-effort taskbar pin/unpin. Windows deliberately hides the "Pin to taskbar" verb
/// from programs, so this uses the documented-in-the-wild workaround: temporarily
/// register Explorer's own pin command handler (GUID read from HKLM's CommandStore at
/// runtime, never hardcoded) as a verb on the exe, invoke it via Shell.Application,
/// then remove the temp verb. Success is verified against the User Pinned\TaskBar
/// folder; when Windows blocks the trick (some Win11 builds), callers get false and
/// should show the "pin it manually" hint instead of pretending it worked.
/// </summary>
public class TaskbarPinService
{
    private const string CommandStore =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell";
    private const string TempVerb = "ghast.taskbarpin";

    private static string PinnedDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    public bool IsPinned()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            return File.Exists(Path.Combine(PinnedDir, "Ghast.lnk"));
        }
        catch (Exception ex)
        {
            Logger.Error("checking taskbar pin", ex);
            return false;
        }
    }

    /// <summary>Returns true only when the pinned state was verified to have changed.</summary>
    public bool SetPinned(bool pin)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        if (IsPinned() == pin)
            return true;

        var storeVerb = pin ? "Windows.taskbarpin" : "Windows.taskbarunpin";
        string? handler;
        using (var key = Registry.LocalMachine.OpenSubKey($@"{CommandStore}\{storeVerb}"))
        {
            handler = key?.GetValue("ExplorerCommandHandler") as string;
        }
        if (string.IsNullOrEmpty(handler))
        {
            Logger.Log($"taskbar pin: no {storeVerb} handler in CommandStore — Windows blocked it");
            return false;
        }

        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe))
            return false;

        var tempKeyPath = $@"Software\Classes\*\shell\{TempVerb}";
        object? shell = null;
        try
        {
            using (var temp = Registry.CurrentUser.CreateSubKey(tempKeyPath, writable: true))
            {
                temp?.SetValue("ExplorerCommandHandler", handler);
            }

            var shellType = Type.GetTypeFromProgID("Shell.Application")
                            ?? throw new InvalidOperationException("Shell.Application unavailable");
            shell = Activator.CreateInstance(shellType);
            dynamic app = shell!;
            var folder = app.Namespace(Path.GetDirectoryName(exe));
            var item = folder?.ParseName(Path.GetFileName(exe));
            if (item is null)
                return false;
            item.InvokeVerb(TempVerb);
        }
        catch (Exception ex)
        {
            Logger.Error("taskbar pin verb", ex);
            return false;
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(tempKeyPath, throwOnMissingSubKey: false); }
            catch { /* best effort cleanup */ }
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }

        // Explorer applies the pin asynchronously; give it a moment, then verify.
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(150);
            if (IsPinned() == pin)
            {
                Logger.Log($"taskbar {(pin ? "pinned" : "unpinned")} ok");
                return true;
            }
        }
        Logger.Log($"taskbar {(pin ? "pin" : "unpin")} could not be verified — Windows likely blocked it");
        return false;
    }
}

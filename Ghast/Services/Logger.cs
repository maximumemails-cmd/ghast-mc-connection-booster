using System.IO;

namespace Ghast.Services;

/// <summary>Append-only session log at %AppData%\Ghast\log.txt. Failures are swallowed: logging must never crash a tweak.</summary>
public static class Logger
{
    private static readonly object Gate = new();

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.AppendAllText(Paths.LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging is best-effort only.
        }
    }

    public static void Error(string context, Exception ex) => Log($"ERROR {context}: {ex}");
}

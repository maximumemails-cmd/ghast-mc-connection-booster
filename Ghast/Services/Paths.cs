using System.IO;

namespace Ghast.Services;

public static class Paths
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ghast");

    public static string PresetsDir => Path.Combine(DataDir, "presets");
    public static string ConfigPath => Path.Combine(DataDir, "config.json");
    public static string BackupPath => Path.Combine(DataDir, "backup.json");
    public static string LogPath => Path.Combine(DataDir, "log.txt");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(PresetsDir);
    }
}

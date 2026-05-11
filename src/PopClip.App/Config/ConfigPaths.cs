using System.IO;

namespace PopClip.App.Config;

internal static class ConfigPaths
{
    public static string ConfigDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PopClip.Win");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string ActionsUserFile => Path.Combine(ConfigDir, "actions.json");
    public static string ActionsBundledFile
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, "actions.json");
        }
    }
}

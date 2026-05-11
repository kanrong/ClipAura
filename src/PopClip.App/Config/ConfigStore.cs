using System.IO;
using System.Text.Json;
using PopClip.Core.Actions;
using PopClip.Core.Logging;

namespace PopClip.App.Config;

/// <summary>从磁盘加载 settings.json / actions.json，并提供缺省回退</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly ILog _log;

    public ConfigStore(ILog log) => _log = log;

    public AppSettings LoadSettings()
    {
        var path = ConfigPaths.SettingsFile;
        if (!File.Exists(path))
        {
            var fresh = new AppSettings();
            SaveSettings(fresh);
            return fresh;
        }
        try
        {
            using var stream = File.OpenRead(path);
            var s = JsonSerializer.Deserialize<AppSettings>(stream, Json);
            return s ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _log.Warn("settings load failed, using defaults", ("err", ex.Message));
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        try
        {
            using var stream = File.Create(ConfigPaths.SettingsFile);
            JsonSerializer.Serialize(stream, settings, Json);
        }
        catch (Exception ex)
        {
            _log.Warn("settings save failed", ("err", ex.Message));
        }
    }

    public ActionsConfig? LoadActions()
    {
        var path = File.Exists(ConfigPaths.ActionsUserFile)
            ? ConfigPaths.ActionsUserFile
            : ConfigPaths.ActionsBundledFile;
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ActionsConfig>(stream, Json);
        }
        catch (Exception ex)
        {
            _log.Warn("actions config load failed", ("err", ex.Message), ("path", path));
            return null;
        }
    }
}

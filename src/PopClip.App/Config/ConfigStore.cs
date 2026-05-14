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
            var s = JsonSerializer.Deserialize<AppSettings>(stream, Json) ?? new AppSettings();
            MigrateLoadedSettings(s);
            return s;
        }
        catch (Exception ex)
        {
            _log.Warn("settings load failed, using defaults", ("err", ex.Message));
            return new AppSettings();
        }
    }

    /// <summary>加载后做一次幂等迁移。当前只处理一项：
    /// 若 AiEnabled=true 但 AiDefaultActionsSeeded 字段缺失/为 false（旧版本写出的 settings.json），
    /// 视为"已经走过 AI 引导阶段"，把 seeded 直接置 true，
    /// 防止用户后续在设置里删除某条默认 AI 动作再保存时被强制补回</summary>
    private static void MigrateLoadedSettings(AppSettings s)
    {
        if (s.AiEnabled && !s.AiDefaultActionsSeeded)
        {
            s.AiDefaultActionsSeeded = true;
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

    public void SaveActions(ActionsConfig config)
    {
        try
        {
            using var stream = File.Create(ConfigPaths.ActionsUserFile);
            JsonSerializer.Serialize(stream, config, Json);
        }
        catch (Exception ex)
        {
            _log.Warn("actions config save failed", ("err", ex.Message));
        }
    }
}

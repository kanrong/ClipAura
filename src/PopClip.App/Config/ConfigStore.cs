using System.IO;
using System.Text.Json;
using PopClip.Actions.BuiltIn;
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

    /// <summary>把 BuiltInActionSeeds 中尚未出现在 cfg 内、且未被 settings 标记过的内置动作，
    /// 追加为 enabled=false 条目。供动作目录显示的内容与 AppHost 启动期保持一致：
    /// 老用户升级后能在"设置 - 动作"列表里直接看到新增的智能/AI 内置动作，按需开启；
    /// 因为有 SeededBuiltInIds 防重补，用户主动删除某条 seed 出来的动作后下次启动不会复活。
    /// 返回 true 表示 cfg/settings 发生了变化，调用方需要持久化两者</summary>
    public bool SeedMissingBuiltInActions(ActionsConfig cfg, AppSettings settings)
    {
        var changed = false;

        var existingBuiltIn = cfg.Actions
            .Where(a => string.Equals(a.Type, "builtin", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a.BuiltIn))
            .Select(a => a.BuiltIn!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingIds = cfg.Actions
            .Select(a => a.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in BuiltInActionSeeds.All)
        {
            if (existingBuiltIn.Contains(seed.BuiltIn)) continue;
            if (settings.SeededBuiltInIds.Contains(seed.BuiltIn)) continue;

            var id = UniqueId(seed.DescriptorId, existingIds);
            cfg.Actions.Add(new ActionDescriptor
            {
                Id = id,
                Type = "builtin",
                BuiltIn = seed.BuiltIn,
                Title = seed.Title,
                Icon = seed.IconKey,
                IconLocked = true,
                Enabled = seed.DefaultEnabled,
            });
            existingIds.Add(id);
            settings.SeededBuiltInIds.Add(seed.BuiltIn);
            changed = true;
        }

        if (changed)
        {
            _log.Info("builtin actions seeded", ("count", settings.SeededBuiltInIds.Count));
        }
        return changed;
    }

    private static string UniqueId(string seed, HashSet<string> taken)
    {
        if (!taken.Contains(seed)) return seed;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{seed}-{i}";
            if (!taken.Contains(candidate)) return candidate;
        }
        return $"{seed}-{Guid.NewGuid():N}";
    }
}

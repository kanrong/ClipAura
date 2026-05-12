using PopClip.App.Config;
using PopClip.Core.Actions;

namespace PopClip.App.Services;

/// <summary>把 AppSettings 适配为 Core 层的 ISettingsProvider，避免 Core 直接依赖 App 配置类型</summary>
internal sealed class SettingsProvider : ISettingsProvider
{
    private readonly AppSettings _settings;

    public SettingsProvider(AppSettings settings) => _settings = settings;

    public string SearchEngineName => _settings.SearchEngineName;

    public string SearchUrlTemplate => _settings.SearchUrlTemplate;
}

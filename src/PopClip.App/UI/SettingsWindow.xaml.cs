using System.Windows;
using PopClip.App.Config;

namespace PopClip.App.UI;

public partial class SettingsWindow : Window
{
    /// <summary>预设搜索引擎：显示名 → URL 模板。{q} 占位符运行时替换</summary>
    private static readonly (string Name, string Url)[] Presets =
    {
        ("Google", "https://www.google.com/search?q={q}"),
        ("Bing",   "https://www.bing.com/search?q={q}"),
        ("百度",   "https://www.baidu.com/s?wd={q}"),
    };

    private readonly ConfigStore _store;
    private readonly AppSettings _settings;

    public SettingsWindow(ConfigStore store, AppSettings settings)
    {
        _store = store;
        _settings = settings;
        InitializeComponent();
        Bind();
    }

    private void Bind()
    {
        BlacklistRadio.IsChecked = _settings.BlacklistMode;
        WhitelistRadio.IsChecked = !_settings.BlacklistMode;
        FullScreenSuppress.IsChecked = _settings.SuppressOnFullScreen;
        foreach (var p in _settings.ProcessFilter) ProcessList.Items.Add(p);

        DisplayIconAndText.IsChecked = _settings.ToolbarDisplay == ToolbarDisplayMode.IconAndText;
        DisplayIconOnly.IsChecked    = _settings.ToolbarDisplay == ToolbarDisplayMode.IconOnly;
        DisplayTextOnly.IsChecked    = _settings.ToolbarDisplay == ToolbarDisplayMode.TextOnly;

        SearchEngineName.Text = _settings.SearchEngineName;
        SearchUrlTemplate.Text = _settings.SearchUrlTemplate;
        SyncPresetSelection();
    }

    /// <summary>根据当前 Url 模板回选预设；不匹配则归为「自定义」</summary>
    private void SyncPresetSelection()
    {
        var url = _settings.SearchUrlTemplate?.Trim() ?? "";
        var preset = Array.Find(Presets, p => string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase));
        if (preset.Url is null)
        {
            SearchCustom.IsChecked = true;
            return;
        }
        switch (preset.Name)
        {
            case "Google": SearchGoogle.IsChecked = true; break;
            case "Bing":   SearchBing.IsChecked = true; break;
            case "百度":   SearchBaidu.IsChecked = true; break;
            default:       SearchCustom.IsChecked = true; break;
        }
    }

    /// <summary>用户选择预设：把名称与 URL 一并填入文本框，便于直观看到/微调</summary>
    private void OnSearchPresetChanged(object sender, RoutedEventArgs e)
    {
        (string Name, string Url)? hit = null;
        if (SearchGoogle.IsChecked == true) hit = Presets[0];
        else if (SearchBing.IsChecked == true) hit = Presets[1];
        else if (SearchBaidu.IsChecked == true) hit = Presets[2];
        if (hit is null) return;

        SearchEngineName.Text = hit.Value.Name;
        SearchUrlTemplate.Text = hit.Value.Url;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.BlacklistMode = BlacklistRadio.IsChecked == true;
        _settings.SuppressOnFullScreen = FullScreenSuppress.IsChecked == true;
        _settings.ToolbarDisplay = DisplayIconOnly.IsChecked == true
            ? ToolbarDisplayMode.IconOnly
            : DisplayTextOnly.IsChecked == true
                ? ToolbarDisplayMode.TextOnly
                : ToolbarDisplayMode.IconAndText;

        var name = SearchEngineName.Text?.Trim() ?? "";
        var url = SearchUrlTemplate.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(name)) _settings.SearchEngineName = name;
        if (!string.IsNullOrEmpty(url) && url.Contains("{q}", StringComparison.Ordinal))
        {
            _settings.SearchUrlTemplate = url;
        }

        _store.SaveSettings(_settings);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

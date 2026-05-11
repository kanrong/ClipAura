using System.Windows;
using PopClip.App.Config;

namespace PopClip.App.UI;

public partial class SettingsWindow : Window
{
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
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.BlacklistMode = BlacklistRadio.IsChecked == true;
        _settings.SuppressOnFullScreen = FullScreenSuppress.IsChecked == true;
        _store.SaveSettings(_settings);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

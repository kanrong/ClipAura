using System.Windows;
using PopClip.App.Hosting;

namespace PopClip.App;

public partial class App : Application
{
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = new AppHost();
        if (!_host.TryAcquireSingleInstance())
        {
            _host.SignalRunningInstance();
            Shutdown(0);
            return;
        }

        _host.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}

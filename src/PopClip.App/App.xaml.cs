using PopClip.App.Hosting;

namespace PopClip.App;

/// <summary>不在文件顶部 using System.Windows 是因为 UseWindowsForms=true 后
/// System.Windows.Forms.Application 与 System.Windows.Application 同名冲突；
/// 用全限定名规避，避免给文件内每处 Application/StartupEventArgs 加别名</summary>
public partial class App : System.Windows.Application
{
    private AppHost? _host;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
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

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}

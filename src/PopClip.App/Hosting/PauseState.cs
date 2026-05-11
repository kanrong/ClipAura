namespace PopClip.App.Hosting;

/// <summary>共享暂停状态。SessionManager 在处理 candidate 前查询；
/// TrayController 通过菜单切换。改用原子布尔而非 lock，避免任何菜单点击都阻塞 candidate 链路</summary>
internal sealed class PauseState
{
    private int _paused; // 0 = 运行, 1 = 暂停

    public bool IsPaused => Volatile.Read(ref _paused) != 0;

    public void Set(bool paused) => Volatile.Write(ref _paused, paused ? 1 : 0);

    public bool Toggle()
    {
        var current = Volatile.Read(ref _paused);
        var next = current == 0 ? 1 : 0;
        Volatile.Write(ref _paused, next);
        return next != 0;
    }
}

namespace PopClip.Core.Model;

/// <summary>前台窗口快照，会话开始时定格，供文本获取/黑白名单/回写定位使用</summary>
public sealed record ForegroundWindowInfo(
    nint Hwnd,
    int ProcessId,
    string ProcessName,
    string WindowClassName,
    string WindowTitle);

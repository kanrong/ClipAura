namespace PopClip.Core.Session;

/// <summary>会话候选的产生方式，影响防抖时长与文本获取策略</summary>
public enum SelectionTrigger
{
    MouseDrag,
    MouseDoubleClick,
    KeyboardSelection,
    /// <summary>Ctrl+左键点击（无拖动）。语义固定为"想在此粘贴"，跳过文本采集，
    /// 直接弹出仅含"粘贴"按钮的工具条</summary>
    MouseCtrlClick,
}

/// <summary>状态机抛出的候选事件，Selection Session Manager 监听后启动文本获取</summary>
public sealed record SelectionCandidate(
    SelectionTrigger Trigger,
    int X,
    int Y,
    DateTime DetectedAtUtc);

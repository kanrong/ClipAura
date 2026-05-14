namespace PopClip.Core.Session;

/// <summary>会话候选的产生方式，影响防抖时长与文本获取策略</summary>
public enum SelectionTrigger
{
    MouseDrag,
    MouseDoubleClick,
    KeyboardSelection,
    /// <summary>配置的修饰键 + 左键点击（无拖动）。语义固定为"想在此处操作剪贴板"，跳过文本采集</summary>
    MouseModifierClick,
}

/// <summary>状态机抛出的候选事件，Selection Session Manager 监听后启动文本获取</summary>
public sealed record SelectionCandidate(
    SelectionTrigger Trigger,
    int X,
    int Y,
    DateTime DetectedAtUtc);

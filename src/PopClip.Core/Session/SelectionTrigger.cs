namespace PopClip.Core.Session;

/// <summary>会话候选的产生方式，影响防抖时长与文本获取策略</summary>
public enum SelectionTrigger
{
    MouseDrag,
    MouseDoubleClick,
    KeyboardSelection,
}

/// <summary>状态机抛出的候选事件，Selection Session Manager 监听后启动文本获取</summary>
public sealed record SelectionCandidate(
    SelectionTrigger Trigger,
    int X,
    int Y,
    DateTime DetectedAtUtc);

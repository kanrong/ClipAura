namespace PopClip.Core.Session;

/// <summary>来自 Hooks 层的输入事件抽象，避免 Core 引用 Win32 类型</summary>
public abstract record InputEvent(DateTime TimestampUtc);

public sealed record MouseDownEvent(int X, int Y, bool Shift, bool Ctrl, bool Alt, DateTime TimestampUtc) : InputEvent(TimestampUtc);
public sealed record MouseUpEvent(int X, int Y, bool Shift, bool Ctrl, bool Alt, DateTime TimestampUtc) : InputEvent(TimestampUtc);
public sealed record MouseMoveEvent(int X, int Y, bool LeftDown, DateTime TimestampUtc) : InputEvent(TimestampUtc);

/// <summary>键盘事件：仅关心可能产生/破坏选区的按键</summary>
public sealed record KeyEvent(int VirtualKey, bool IsDown, bool Shift, bool Ctrl, bool Alt, DateTime TimestampUtc)
    : InputEvent(TimestampUtc);

/// <summary>前台窗口变化通知，触发会话取消</summary>
public sealed record ForegroundChangedEvent(nint Hwnd, DateTime TimestampUtc) : InputEvent(TimestampUtc);

public enum SelectionPopupMode
{
    Immediate,
    Delayed,
    HoverStill,
    ModifierRequired,
}

public enum SelectionModifierKey
{
    Alt,
    Ctrl,
    Shift,
}

public sealed class SelectionStateOptions
{
    public SelectionPopupMode PopupMode { get; set; } = SelectionPopupMode.Immediate;
    public int PopupDelayMs { get; set; } = 200;
    public int HoverDelayMs { get; set; } = 300;
    public SelectionModifierKey RequiredModifier { get; set; } = SelectionModifierKey.Alt;
}

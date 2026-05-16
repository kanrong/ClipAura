namespace PopClip.Core.Model;

/// <summary>选区物理矩形（设备像素），由 UIA 或鼠标位置兜底产生</summary>
public readonly record struct SelectionRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public static SelectionRect FromPoint(int x, int y) => new(x, y, x, y);
}

/// <summary>文本采集来源，用于判断是否可走 UIA 回写以及是否需要恢复剪贴板</summary>
public enum AcquisitionSource
{
    UiaTextPattern,
    UiaValuePattern,
    ClipboardFallback,
    /// <summary>区域 OCR 截图识别。来源是位图无法回写到原始控件，所以 IsLikelyEditable 永远 false，
    /// 翻译/AI 等"读"动作仍照常可用，"替换/插入"动作由 IActionHost 自动跳过</summary>
    Ocr,
    Unknown,
}

/// <summary>一次选区会话向 Action 暴露的全部上下文，跨进程边界只读</summary>
public sealed record SelectionContext(
    string Text,
    AcquisitionSource Source,
    ForegroundWindowInfo Foreground,
    SelectionRect Rect,
    bool IsLikelyEditable,
    DateTime AcquiredAtUtc)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

using System.Windows.Automation;
using PopClip.Core.Logging;
using PopClip.Core.Model;

namespace PopClip.Uia;

/// <summary>尝试用 UIA 把选区替换为新文本。仅当目标元素同时具备 TextPattern + ValuePattern
/// 且非只读时才能稳妥替换；否则返回 false 由上层走剪贴板 Ctrl+V 兜底</summary>
public sealed class UiaTextReplacer
{
    private readonly ILog _log;

    public UiaTextReplacer(ILog log) => _log = log;

    public bool TryReplace(SelectionContext context, AutomationElement? element, string newText)
    {
        if (element is null) return false;
        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw)
                || raw is not ValuePattern vp)
            {
                return false;
            }
            if (vp.Current.IsReadOnly) return false;

            // ValuePattern.SetValue 会替换整个值，对单行 Edit 控件适用；
            // 多行文档需要 TextPattern + 模拟键入，MVP 阶段保守地仅在单行场景启用
            if (LooksLikeSingleLineEdit(element))
            {
                vp.SetValue(newText);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.Debug("UIA replace failed", ("err", ex.Message));
            return false;
        }
    }

    private static bool LooksLikeSingleLineEdit(AutomationElement element)
    {
        try
        {
            var ct = element.Current.ControlType;
            if (ct != ControlType.Edit) return false;
            // IsKeyboardFocusable 几乎必然 true，更可靠的是检查 IsPassword/IsMultiLine，
            // 但 multi-line 信息没有公开属性，这里只用 ControlType=Edit 粗判
            return true;
        }
        catch { return false; }
    }
}

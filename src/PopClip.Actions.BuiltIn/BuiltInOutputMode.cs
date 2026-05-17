using System;
using System.Threading;
using System.Threading.Tasks;
using PopClip.Core.Actions;
using PopClip.Core.Model;

namespace PopClip.Actions.BuiltIn;

/// <summary>智能动作（CSV/MD/JSON/TSV 等）的结果落点模式。
/// 与 AiOutputMode 平行存在：AI 模式是给 AI 模板用的（chat / replace / clipboard / inlineToast），
/// 而 BuiltInOutputMode 是给"内置 + 有产出结果"的动作用的。
///
/// 设计目标：让用户对"格式化 JSON 后该把结果丢到哪里"有完整控制权 —
/// 短结果想看一眼就走 Bubble，长结果（如多页 CSV）走 Dialog，
/// 要立刻粘到别处就走 Copy，最常用的 CopyAndBubble 保留剪贴板兜底 + 视觉确认</summary>
public enum BuiltInOutputMode
{
    /// <summary>仅写剪贴板，不弹任何窗口。等同于"复制到剪贴板"按钮</summary>
    Copy,
    /// <summary>只显示气泡（AiBubbleWindow 同款）；不写剪贴板</summary>
    Bubble,
    /// <summary>同时复制 + 气泡。默认值，照顾"既要看到又要能粘走"的高频路径</summary>
    CopyAndBubble,
    /// <summary>独立结果窗口（SmartResultWindow）。适合长内容、需要在窗口里反复滚动/复制片段</summary>
    Dialog,
}

/// <summary>BuiltInOutputMode 的解析与回写。
/// 复用 ActionDescriptor.OutputMode（string 字段）做存储，键名与枚举值小写一致，
/// 兼容历史空值（按 default 走）</summary>
public static class BuiltInOutputModes
{
    public const string Copy = "copy";
    public const string Bubble = "bubble";
    public const string CopyAndBubble = "copyAndBubble";
    public const string Dialog = "dialog";

    /// <summary>读取 descriptor.OutputMode，无效或空时返回 fallback。
    /// 不抛异常：用户手改 actions.json 写错了字符串时也能优雅降级</summary>
    public static BuiltInOutputMode Parse(string? value, BuiltInOutputMode fallback = BuiltInOutputMode.CopyAndBubble)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.Trim() switch
        {
            Copy => BuiltInOutputMode.Copy,
            Bubble => BuiltInOutputMode.Bubble,
            CopyAndBubble => BuiltInOutputMode.CopyAndBubble,
            Dialog => BuiltInOutputMode.Dialog,
            _ => fallback,
        };
    }

    public static string ToKey(BuiltInOutputMode mode) => mode switch
    {
        BuiltInOutputMode.Copy => Copy,
        BuiltInOutputMode.Bubble => Bubble,
        BuiltInOutputMode.CopyAndBubble => CopyAndBubble,
        BuiltInOutputMode.Dialog => Dialog,
        _ => CopyAndBubble,
    };

    /// <summary>判断指定动作是否支持配置 OutputMode。
    /// 仅"有文本产出"的动作适用：JsonFormat / JsonToYaml / Color / Timestamp / CSV ↔ MD / TSV ↔ CSV / OCR 段落整理 / WordCount / Calculate；
    /// 不适用：Copy（自身就是写剪贴板）、Paste、Search、Translate（外部跳转）、PathOpen（动作就是打开资源管理器）、所有 AI 模板（自带 AiOutputMode）、ClipboardHistory</summary>
    public static bool SupportsOutputMode(string actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return false;
        return actionId switch
        {
            BuiltInActionIds.JsonFormat
            or BuiltInActionIds.JsonToYaml
            or BuiltInActionIds.Color
            or BuiltInActionIds.Timestamp
            or BuiltInActionIds.MarkdownTableToCsv
            or BuiltInActionIds.CsvToMarkdown
            or BuiltInActionIds.TsvToCsv
            or BuiltInActionIds.TsvToMarkdown
            or BuiltInActionIds.OcrParagraphTidy
            or BuiltInActionIds.WordCount
            or BuiltInActionIds.Calculate => true,
            _ => false,
        };
    }
}

/// <summary>智能动作发布结果的统一入口：
/// 一段已就绪文本 + descriptor + IActionHost → 按 OutputMode 路由到对应展示通道。
/// 抽出公共逻辑，避免每个 SmartAction 各自重复"if copy / if bubble / if dialog" 模板。
///
/// 区分 primaryText 与 displayText 是给"摘要 vs 主结果"两种语义不同的动作用的：
/// - JsonFormat：两者相同（格式化后的 JSON 同时被复制和展示）
/// - Color：primary=HEX 字符串（粘出来直接能用），display=多格式 summary（人眼对照看）</summary>
public static class SmartOutput
{
    /// <param name="primaryText">写入剪贴板的内容</param>
    /// <param name="displayText">气泡 / 对话框展示的内容；为 null 时复用 primaryText</param>
    /// <param name="copyToast">Copy 模式下的 toast 文本；为 null 时用 "{title} ✓（已复制）"</param>
    /// <remarks>
    /// 设计要点：把 SelectionContext 透传进来（而不是只传 referenceText 字符串）的目的：
    /// - referenceText 仍由 context.Text 派生，调用方不再各自重复 context.Text
    /// - 气泡模式下，若选区可编辑（IsLikelyEditable），自动构造"把结果写回原选区"的 onReplace
    ///   回调传给 Bubble，让用户在 JSON 格式化 / CSV 转换等场景能直接"替换原文"
    /// - 不可编辑选区（OCR / 只读 UI 文本）会自然不出现"替换"按钮，避免误导
    /// </remarks>
    public static void Publish(
        IActionHost host,
        SelectionContext context,
        string title,
        string primaryText,
        string? displayText = null,
        string? copyToast = null,
        BuiltInOutputMode fallback = BuiltInOutputMode.CopyAndBubble)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        if (context is null) throw new ArgumentNullException(nameof(context));
        var mode = BuiltInOutputModes.Parse(host.Descriptor?.OutputMode, fallback);
        var safePrimary = primaryText ?? "";
        var safeDisplay = displayText ?? safePrimary;
        var safeToast = copyToast ?? $"{title} ✓（已复制）";
        var referenceText = context.Text ?? "";

        // 是否暴露"替换原文"按钮，与 AiTextService 的翻译气泡保持一致：
        // - !IsEmpty 才有可替换的"原文"
        // - 排除 Source=Ocr：OCR 来源没有"原选区"概念，剪贴板兜底粘贴会粘到 OCR 截图时的前台窗口，
        //   并非用户期望的目的地
        // - 不再判 IsLikelyEditable：TextReplacerService 内部先尝试 UIA SetValue，
        //   失败自动回退到 ClipboardPaste（剪贴板替换 + SendInput Ctrl+V），
        //   只读 / 无 ValuePattern 的浏览器 / 编辑器仍能由剪贴板兜底完成替换
        Func<string, Task>? replaceCallback = null;
        if (!context.IsEmpty && context.Source != AcquisitionSource.Ocr && host.Replacer is not null)
        {
            replaceCallback = async newText =>
            {
                await host.Replacer.TryReplaceAsync(context, newText, CancellationToken.None).ConfigureAwait(false);
            };
        }

        // bubble/dialog 系也做"剪贴板兜底"：用户误关窗口后想再粘贴时仍能找到内容
        switch (mode)
        {
            case BuiltInOutputMode.Copy:
                host.Clipboard.SetText(safePrimary);
                host.Notifier.Notify(safeToast);
                break;
            case BuiltInOutputMode.Bubble:
                if (host.Bubble is not null)
                {
                    host.Bubble.ShowStatic(title, safeDisplay,
                        canReplace: replaceCallback is not null,
                        onReplace: replaceCallback);
                }
                else
                {
                    host.Clipboard.SetText(safePrimary);
                    host.Notifier.Notify(safeToast);
                }
                break;
            case BuiltInOutputMode.CopyAndBubble:
                host.Clipboard.SetText(safePrimary);
                if (host.Bubble is not null)
                {
                    host.Bubble.ShowStatic(title, safeDisplay,
                        canReplace: replaceCallback is not null,
                        onReplace: replaceCallback);
                }
                else host.Notifier.Notify(safeToast);
                break;
            case BuiltInOutputMode.Dialog:
                host.Clipboard.SetText(safePrimary);
                if (host.ResultDialog is not null) host.ResultDialog.Show(title, referenceText, safeDisplay);
                else host.Notifier.Notify(safeToast);
                break;
        }
    }
}

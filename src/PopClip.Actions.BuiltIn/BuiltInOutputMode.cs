using System;
using System.Threading;
using System.Threading.Tasks;
using PopClip.Core.Actions;
using PopClip.Core.Model;

namespace PopClip.Actions.BuiltIn;

/// <summary>智能动作（CSV/MD/JSON/TSV 等）的结果落点模式。
/// 与 AiOutputMode 平行存在：AI 模式是给 AI 模板用的（chat / replace / clipboard / toast / bubble），
/// 而 BuiltInOutputMode 是给"内置 + 有产出结果"的动作用的。
///
/// 命名上与 AiOutputMode 对齐——"相同行为同名"原则：
/// - Toast 与 AiOutputMode.Toast 行为一致（写剪贴板 + toast 提示，自动消失）
/// - Bubble 与 AiOutputMode.Bubble 行为一致（仅气泡）
/// - ClipboardAndBubble 借用 AiOutputMode.Clipboard 的"剪贴板兜底"概念组合 Bubble
/// - Dialog 是内置侧独有：用 SmartResultWindow 承载长内容
///
/// 设计目标：让用户对"格式化 JSON 后该把结果丢到哪里"有完整控制权 —
/// 短结果走 Toast（顺带复制），中等结果走 Bubble（不打扰剪贴板），
/// 高频路径走 ClipboardAndBubble（视觉确认 + 剪贴板兜底），长内容走 Dialog</summary>
public enum BuiltInOutputMode
{
    /// <summary>写剪贴板 + Toast 提示（自动消失）。
    /// 与 AiOutputMode.Toast 行为一致；调用方可通过 SmartOutput.Publish 的 copyToast 自定义 toast 文案，
    /// 例如把"格式化 JSON ✓（已复制）"换成"2+3 = 5（已复制）"以直接显示结果</summary>
    Toast,
    /// <summary>只显示气泡（AiBubbleWindow 同款）；不写剪贴板。
    /// 与 AiOutputMode.Bubble 行为一致</summary>
    Bubble,
    /// <summary>写剪贴板 + 气泡。默认值，照顾"既要看到又要能粘走"的高频路径。
    /// 与 AiOutputMode.Clipboard + Bubble 的组合概念呼应——"剪贴板兜底 + 主显示走气泡"</summary>
    ClipboardAndBubble,
    /// <summary>独立结果窗口（SmartResultWindow）。适合长内容、需要在窗口里反复滚动/复制片段</summary>
    Dialog,
}

/// <summary>BuiltInOutputMode 的解析与回写。
/// 复用 ActionDescriptor.OutputMode（string 字段）做存储，键名与枚举值小写驼峰一致</summary>
public static class BuiltInOutputModes
{
    public const string Toast = "toast";
    public const string Bubble = "bubble";
    public const string ClipboardAndBubble = "clipboardAndBubble";
    public const string Dialog = "dialog";

    /// <summary>读取 descriptor.OutputMode，无效或空时返回 fallback。
    /// 不抛异常：用户手改 actions.json 写错了字符串时也能优雅降级</summary>
    public static BuiltInOutputMode Parse(string? value, BuiltInOutputMode fallback = BuiltInOutputMode.ClipboardAndBubble)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.Trim() switch
        {
            Toast => BuiltInOutputMode.Toast,
            Bubble => BuiltInOutputMode.Bubble,
            ClipboardAndBubble => BuiltInOutputMode.ClipboardAndBubble,
            Dialog => BuiltInOutputMode.Dialog,
            _ => fallback,
        };
    }

    public static string ToKey(BuiltInOutputMode mode) => mode switch
    {
        BuiltInOutputMode.Toast => Toast,
        BuiltInOutputMode.Bubble => Bubble,
        BuiltInOutputMode.ClipboardAndBubble => ClipboardAndBubble,
        BuiltInOutputMode.Dialog => Dialog,
        _ => ClipboardAndBubble,
    };

    /// <summary>判断指定动作是否支持配置 OutputMode。
    /// 仅"有文本产出"的动作适用：JsonFormat / JsonToYaml / Color / Timestamp / CSV ↔ MD / TSV ↔ CSV / 查词 / 词汇解析 / WordCount / Calculate；
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
            or BuiltInActionIds.WordLookup
            or BuiltInActionIds.VocabularyAnalyze
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
        BuiltInOutputMode fallback = BuiltInOutputMode.ClipboardAndBubble)
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

        // ClipboardAndBubble / Dialog 都做"剪贴板兜底"：用户误关窗口后想再粘贴时仍能找到内容；
        // Bubble 单选不做兜底，保持"用户想要的就是不打扰剪贴板"的语义稳定
        switch (mode)
        {
            case BuiltInOutputMode.Toast:
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
            case BuiltInOutputMode.ClipboardAndBubble:
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

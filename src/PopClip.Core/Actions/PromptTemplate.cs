using System.Text.RegularExpressions;
using PopClip.Core.Model;

namespace PopClip.Core.Actions;

/// <summary>把用户在 actions.json 里写的 prompt 模板按选区上下文展开。
///
/// 支持的占位符（区分大小写）：
///   {text}            - 选区文本原文
///   {language}        - 当前 AI 默认语言（由 ISettingsProvider 提供）
///   {clipboard}       - 当前剪贴板文本（由 host 提供，可能为空）
///   {foreground_proc} - 前台进程名
///   {selection_len}   - 选区长度（字符数）
///   {date}            - YYYY-MM-DD
///   {time}            - HH:mm
///
/// 设计取舍：故意只支持最常用的几个变量。复杂表达式留给 v2，避免变成迷你模板语言</summary>
public static class PromptTemplate
{
    private static readonly Regex VariableRegex = new(@"\{([a-zA-Z_][a-zA-Z0-9_]*)\}", RegexOptions.Compiled);

    public static string Expand(string template, PromptVariables vars)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var lookup = BuildLookup(vars);
        return VariableRegex.Replace(template, m =>
            lookup.TryGetValue(m.Groups[1].Value, out var value)
                ? value
                : m.Value);
    }

    /// <summary>当 prompt 中没有任何 {text} 占位符时，将选区文本以"参考文本"段落形式追加到结尾。
    /// 让用户写"请翻译"也能自然拿到选区，而不必每个 prompt 都写 {text}</summary>
    public static string EnsureSelectionUsed(string expanded, string selectionText, string language)
    {
        if (string.IsNullOrWhiteSpace(selectionText)) return expanded;
        if (expanded.Contains(selectionText, StringComparison.Ordinal)) return expanded;
        var lead = string.IsNullOrWhiteSpace(language) ? "中文" : language.Trim();
        return expanded.TrimEnd() + $"\n\n以下是选中文本，请以{lead}回应：\n\n{selectionText}";
    }

    private static Dictionary<string, string> BuildLookup(PromptVariables v)
    {
        var now = DateTime.Now;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["text"] = v.Text,
            ["language"] = v.Language,
            ["clipboard"] = v.Clipboard,
            ["foreground_proc"] = v.ForegroundProcess,
            ["selection_len"] = v.Text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["date"] = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["time"] = now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}

public sealed record PromptVariables(
    string Text,
    string Language,
    string Clipboard,
    string ForegroundProcess)
{
    public static PromptVariables From(SelectionContext context, string language, string clipboard)
        => new(
            context.Text ?? "",
            string.IsNullOrWhiteSpace(language) ? "中文" : language.Trim(),
            clipboard ?? "",
            context.Foreground.ProcessName ?? "");
}

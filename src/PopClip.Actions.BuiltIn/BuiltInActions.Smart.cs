using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PopClip.Core.Actions;
using PopClip.Core.Model;

namespace PopClip.Actions.BuiltIn;

/// <summary>"文本类型智能动作链"使用的 IconKey。
/// 与 BuiltInActionIds 一样集中存放方便排查；图标本身在 IconKeyToGlyphConverter / IconKeyToMaterialDesignKindConverter 注册。
///
/// 每个智能动作必须使用唯一的 IconKey：
/// 浮窗里多个智能动作同时出现时（如选中 JSON 时显示"格式化 JSON"和"JSON → YAML"），
/// 视觉上必须能一眼区分。带"X→Y"语义的转换动作按"输出端"语义选图标</summary>
public static class SmartActionIcons
{
    public const string Json = "Json";              // JsonFormat: 代码块
    public const string JsonToYaml = "JsonToYaml";  // 双向箭头表示格式间转换
    public const string Color = "Color";            // 调色板
    public const string Time = "Time";              // 时钟
    public const string FolderOpen = "FolderOpen";  // 打开文件夹
    public const string Table = "Table";            // CsvToMarkdown / TsvToMarkdown 之外的"目标=表格"动作
    public const string MdToCsv = "MdToCsv";        // MD 表→CSV，输出是一维清单
    public const string TsvToCsv = "TsvToCsv";      // TSV→CSV，输出也是清单（与 MdToCsv 视觉再区分）
    public const string TsvToMd = "TsvToMd";        // TSV→MD 表，输出是表（与 Table 视觉再区分）
    public const string Dictionary = "Dictionary";  // 离线查词
    public const string Vocabulary = "Vocabulary";  // 段落词汇解析
}

/// <summary>JSON 格式化动作。
/// CanRun 用 trim 后首末字符 + JsonDocument.TryParse 三段过滤，避免对普通文本误触发；
/// 输出走剪贴板 + toast，不直接替换原文，保留用户原选区</summary>
internal sealed class JsonFormatAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.JsonFormat;
    public override string Title => "格式化 JSON";
    public override string IconKey => SmartActionIcons.Json;

    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty && SmartTextProbes.LooksLikeJson(context.Text);

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        try
        {
            var trimmed = context.Text.Trim();
            using var doc = JsonDocument.Parse(trimmed);
            var formatted = JsonSerializer.Serialize(
                doc.RootElement,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            SmartOutput.Publish(host, context, Title, formatted,
                copyToast: $"JSON 已格式化（{CountLines(formatted)} 行，已复制）");
            host.Log.Info("json formatted", ("lines", CountLines(formatted)));
        }
        catch (Exception ex)
        {
            host.Notifier.Notify($"JSON 格式化失败：{ex.Message}");
            host.Log.Warn("json format failed", ("err", ex.Message));
        }
        return Task.CompletedTask;
    }

    private static int CountLines(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        return s.Count(c => c == '\n') + 1;
    }
}

/// <summary>JSON → YAML 浅层转换。
/// 不引入 YamlDotNet，手写支持 scalar/array/object/null/bool/number/string。
/// 复杂场景（多行字符串、引用、anchors）留给 v2，避免一上来就堆依赖</summary>
internal sealed class JsonToYamlAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.JsonToYaml;
    public override string Title => "JSON → YAML";
    public override string IconKey => SmartActionIcons.JsonToYaml;

    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty && SmartTextProbes.LooksLikeJson(context.Text);

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(context.Text.Trim());
            var sb = new StringBuilder();
            JsonToYamlWriter.Write(doc.RootElement, sb, indentLevel: 0, isRoot: true);
            var yaml = sb.ToString().TrimEnd('\n');
            SmartOutput.Publish(host, context, Title, yaml, copyToast: "已转为 YAML（已复制）");
        }
        catch (Exception ex)
        {
            host.Notifier.Notify($"YAML 转换失败：{ex.Message}");
            host.Log.Warn("json to yaml failed", ("err", ex.Message));
        }
        return Task.CompletedTask;
    }
}

/// <summary>颜色码识别 + 多格式互转。
/// 支持 #RGB / #RRGGBB / #RRGGBBAA / rgb(...) / rgba(...)；HSL 由 RGB 派生。
/// 结果以一行 toast 展示，方便用户拷贝其中需要的一种格式</summary>
internal sealed class ColorAction : BuiltInAction
{
    private static readonly Regex HexRegex = new(@"^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);
    private static readonly Regex RgbRegex = new(
        @"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*([01]?(?:\.\d+)?|0|1)\s*)?\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public override string Id => BuiltInActionIds.Color;
    public override string Title => "颜色";
    public override string IconKey => SmartActionIcons.Color;

    public override bool CanRun(SelectionContext context)
    {
        if (context.IsEmpty) return false;
        var t = context.Text.Trim();
        return HexRegex.IsMatch(t) || RgbRegex.IsMatch(t);
    }

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var t = context.Text.Trim();
        if (!TryParse(t, out var r, out var g, out var b, out var a))
        {
            host.Notifier.Notify("颜色解析失败");
            return Task.CompletedTask;
        }

        var hex = a < 255 ? $"#{r:X2}{g:X2}{b:X2}{a:X2}" : $"#{r:X2}{g:X2}{b:X2}";
        var rgb = a < 255
            ? $"rgba({r}, {g}, {b}, {(a / 255.0):0.##})"
            : $"rgb({r}, {g}, {b})";
        var (h, s, l) = RgbToHsl(r, g, b);
        var hsl = $"hsl({h:0}, {s:0}%, {l:0}%)";

        var summary = $"{hex}\n{rgb}\n{hsl}";
        var inlineSummary = $"{hex}  ·  {rgb}  ·  {hsl}";
        SmartOutput.Publish(host, context, Title,
            primaryText: hex,
            displayText: summary,
            copyToast: inlineSummary + "（HEX 已复制）");
        host.Log.Info("color parsed", ("r", r), ("g", g), ("b", b), ("a", a));
        return Task.CompletedTask;
    }

    /// <summary>解析常见颜色字面量到 RGBA8，alpha 缺省按 255 填充。
    /// 对 #RGB 短写做 nibble 扩展（#ABC → #AABBCC），与 CSS 解析行为一致</summary>
    private static bool TryParse(string text, out int r, out int g, out int b, out int a)
    {
        r = g = b = 0;
        a = 255;
        var hex = HexRegex.Match(text);
        if (hex.Success)
        {
            var v = hex.Groups[1].Value;
            switch (v.Length)
            {
                case 3:
                    r = HexNibble(v[0]) * 17;
                    g = HexNibble(v[1]) * 17;
                    b = HexNibble(v[2]) * 17;
                    return true;
                case 6:
                    r = int.Parse(v.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    g = int.Parse(v.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    b = int.Parse(v.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    return true;
                case 8:
                    r = int.Parse(v.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    g = int.Parse(v.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    b = int.Parse(v.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    a = int.Parse(v.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    return true;
            }
        }
        var rgb = RgbRegex.Match(text);
        if (rgb.Success)
        {
            r = Math.Clamp(int.Parse(rgb.Groups[1].Value, CultureInfo.InvariantCulture), 0, 255);
            g = Math.Clamp(int.Parse(rgb.Groups[2].Value, CultureInfo.InvariantCulture), 0, 255);
            b = Math.Clamp(int.Parse(rgb.Groups[3].Value, CultureInfo.InvariantCulture), 0, 255);
            if (rgb.Groups[4].Success
                && double.TryParse(rgb.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
            {
                a = Math.Clamp((int)Math.Round(alpha * 255), 0, 255);
            }
            return true;
        }
        return false;
    }

    private static int HexNibble(char c)
        => c is >= '0' and <= '9' ? c - '0'
         : c is >= 'a' and <= 'f' ? c - 'a' + 10
         : c is >= 'A' and <= 'F' ? c - 'A' + 10
         : 0;

    /// <summary>RGB(0-255) → HSL(度/百分比)。
    /// 算法即 CSS Color 4 / Wikipedia HSL 公式，浮点误差不影响 toast 展示精度</summary>
    private static (double H, double S, double L) RgbToHsl(int r, int g, int b)
    {
        var rn = r / 255.0;
        var gn = g / 255.0;
        var bn = b / 255.0;
        var max = Math.Max(rn, Math.Max(gn, bn));
        var min = Math.Min(rn, Math.Min(gn, bn));
        var l = (max + min) / 2.0;
        if (Math.Abs(max - min) < 1e-9)
        {
            return (0, 0, l * 100);
        }
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (Math.Abs(max - rn) < 1e-9)
        {
            h = (gn - bn) / d + (gn < bn ? 6 : 0);
        }
        else if (Math.Abs(max - gn) < 1e-9)
        {
            h = (bn - rn) / d + 2;
        }
        else
        {
            h = (rn - gn) / d + 4;
        }
        h *= 60;
        return (h, s * 100, l * 100);
    }
}

/// <summary>时间戳识别：10 位（秒）/ 13 位（毫秒），数值范围限制 2000-01-01 到 2099-12-31。
/// 数值范围过滤是为了避免年份 / 行号 / 普通数字被误判（10 位 "2026" 不在范围内）</summary>
internal sealed class TimestampAction : BuiltInAction
{
    private static readonly Regex DigitsOnly = new(@"^\d{10}$|^\d{13}$", RegexOptions.Compiled);
    private const long MinSec = 946_684_800L;       // 2000-01-01 UTC
    private const long MaxSec = 4_102_444_800L;     // 2100-01-01 UTC

    public override string Id => BuiltInActionIds.Timestamp;
    public override string Title => "时间戳";
    public override string IconKey => SmartActionIcons.Time;

    public override bool CanRun(SelectionContext context)
    {
        if (context.IsEmpty) return false;
        var t = context.Text.Trim();
        if (!DigitsOnly.IsMatch(t)) return false;
        if (!long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
        var seconds = t.Length == 13 ? value / 1000 : value;
        return seconds is >= MinSec and < MaxSec;
    }

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var t = context.Text.Trim();
        if (!long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var raw))
        {
            host.Notifier.Notify("时间戳解析失败");
            return Task.CompletedTask;
        }
        var seconds = t.Length == 13 ? raw / 1000 : raw;
        var dto = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var local = dto.ToLocalTime();
        var utc = dto.UtcDateTime;
        var span = DateTime.UtcNow - utc;
        var relative = FormatRelative(span);
        var localStr = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var summary = $"{localStr}（本地）\n{utc:yyyy-MM-dd HH:mm:ss} UTC\n{relative}";
        var inlineSummary = $"{localStr}（本地） · {utc:yyyy-MM-dd HH:mm:ss} UTC · {relative}";

        SmartOutput.Publish(host, context, Title,
            primaryText: localStr,
            displayText: summary,
            copyToast: inlineSummary + "（本地时间已复制）");
        return Task.CompletedTask;
    }

    private static string FormatRelative(TimeSpan span)
    {
        var abs = span.Duration();
        var future = span.TotalSeconds < 0;
        string label;
        if (abs.TotalDays >= 365) label = $"{abs.TotalDays / 365:0.0} 年";
        else if (abs.TotalDays >= 30) label = $"{abs.TotalDays / 30:0.0} 月";
        else if (abs.TotalDays >= 1) label = $"{abs.TotalDays:0.0} 天";
        else if (abs.TotalHours >= 1) label = $"{abs.TotalHours:0.0} 小时";
        else if (abs.TotalMinutes >= 1) label = $"{abs.TotalMinutes:0} 分钟";
        else label = $"{abs.TotalSeconds:0} 秒";
        return future ? label + "后" : label + "前";
    }
}

/// <summary>Windows 文件路径识别：盘符路径或 UNC 路径，存在性检查留到 Run 时。
/// 路径包含双引号 / 制表符等异常字符直接拒绝，避免被 explorer.exe /select 注入解释</summary>
internal sealed class PathAction : BuiltInAction
{
    private static readonly Regex PathRegex = new(
        @"^([a-zA-Z]:[\\/][^""<>|\r\n]*|\\\\[^\\""<>|\r\n]+\\[^""<>|\r\n]+)$",
        RegexOptions.Compiled);

    public override string Id => BuiltInActionIds.PathOpen;
    public override string Title => "在资源管理器打开";
    public override string IconKey => SmartActionIcons.FolderOpen;

    public override bool CanRun(SelectionContext context)
    {
        if (context.IsEmpty) return false;
        var t = context.Text.Trim();
        if (t.Length is < 3 or > 1024) return false;
        return PathRegex.IsMatch(t);
    }

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var path = context.Text.Trim().Trim('"');
        try
        {
            if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
            {
                // /select 会高亮选中目标；目录路径 explorer 同样接受，会在父目录中高亮
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                host.Notifier.Notify("已在资源管理器中定位");
                return Task.CompletedTask;
            }
            // 路径不存在时退而打开父目录，避免完全无反馈
            var parent = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && System.IO.Directory.Exists(parent))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + parent + "\"") { UseShellExecute = true });
                host.Notifier.Notify("目标不存在，已打开父目录");
            }
            else
            {
                host.Notifier.Notify("路径不存在，无法打开");
            }
        }
        catch (Exception ex)
        {
            host.Notifier.Notify("打开失败：" + ex.Message);
            host.Log.Warn("path open failed", ("err", ex.Message), ("path", path));
        }
        return Task.CompletedTask;
    }
}

/// <summary>Markdown 表格 → CSV。
/// 识别"两行以上 + 分隔行匹配 |---|---|"作为最强信号；不含分隔行的伪表格主动拒绝，避免误转换</summary>
internal sealed class MarkdownTableToCsvAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.MarkdownTableToCsv;
    public override string Title => "MD 表格 → CSV";
    public override string IconKey => SmartActionIcons.MdToCsv;

    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty && SmartTextProbes.LooksLikeMarkdownTable(context.Text);

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var rows = ParseMarkdownTable(context.Text);
        if (rows.Count == 0)
        {
            host.Notifier.Notify("未识别到 Markdown 表格");
            return Task.CompletedTask;
        }
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(QuoteCsvCell(row[i]));
            }
            sb.Append('\n');
        }
        var csv = sb.ToString().TrimEnd('\n');
        SmartOutput.Publish(host, context, Title, csv,
            copyToast: $"已转为 CSV（{rows.Count} 行 × {rows[0].Count} 列，已复制）");
        return Task.CompletedTask;
    }

    private static List<List<string>> ParseMarkdownTable(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new List<List<string>>();
        var skipped = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|')) continue;
            // 分隔行（|---|---|）不入结果集；只剩"是否首次出现"用于跳过 header 之后的对齐行
            if (IsSeparatorRow(trimmed))
            {
                skipped++;
                continue;
            }
            var cells = SplitRow(trimmed);
            result.Add(cells);
        }
        return skipped > 0 ? result : new List<List<string>>();
    }

    private static bool IsSeparatorRow(string line)
    {
        var inner = line.Trim('|');
        var cells = inner.Split('|');
        if (cells.Length == 0) return false;
        foreach (var c in cells)
        {
            var t = c.Trim();
            // 合法的 alignment 标记：可选 : + 多个 - + 可选 :
            if (!Regex.IsMatch(t, @"^:?-+:?$")) return false;
        }
        return true;
    }

    private static List<string> SplitRow(string line)
    {
        var inner = line.Trim('|');
        return inner.Split('|').Select(s => s.Trim()).ToList();
    }

    private static string QuoteCsvCell(string cell)
    {
        if (cell.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return cell;
        return "\"" + cell.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>CSV → Markdown 表格。
/// 严格识别条件：≥2 行 + 每行至少 1 个逗号 + 各行逗号数量一致；
/// 这样能避免把"自然语言含逗号的多段文字"误判为 CSV</summary>
internal sealed class CsvToMarkdownAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.CsvToMarkdown;
    public override string Title => "CSV → MD 表格";
    public override string IconKey => SmartActionIcons.Table;

    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty && SmartTextProbes.LooksLikeCsv(context.Text);

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var rows = ParseCsv(context.Text);
        if (rows.Count < 2)
        {
            host.Notifier.Notify("CSV 行数不足，无法构造表格");
            return Task.CompletedTask;
        }
        var sb = new StringBuilder();
        var width = rows[0].Count;
        sb.Append('|');
        foreach (var c in rows[0]) sb.Append(' ').Append(EscapeMdCell(c)).Append(" |");
        sb.Append('\n').Append('|');
        for (var i = 0; i < width; i++) sb.Append(" --- |");
        sb.Append('\n');
        for (var r = 1; r < rows.Count; r++)
        {
            sb.Append('|');
            for (var i = 0; i < width; i++)
            {
                var cell = i < rows[r].Count ? rows[r][i] : "";
                sb.Append(' ').Append(EscapeMdCell(cell)).Append(" |");
            }
            sb.Append('\n');
        }
        var md = sb.ToString().TrimEnd('\n');
        SmartOutput.Publish(host, context, Title, md,
            copyToast: $"已转为 Markdown 表格（{rows.Count} 行 × {width} 列，已复制）");
        return Task.CompletedTask;
    }

    private static List<List<string>> ParseCsv(string text)
    {
        // 这里只做"无引号 + 标准逗号分隔"的最小实现；
        // 含引号嵌套 / 转义 / 引号内换行的复杂 CSV 留给 v2，第一版聚焦"日常表格粘贴"场景
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var rows = new List<List<string>>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            rows.Add(line.Split(',').Select(s => s.Trim()).ToList());
        }
        NormalizeShortCsvRows(rows);
        return rows;
    }

    private static void NormalizeShortCsvRows(List<List<string>> rows)
    {
        if (rows.Count < 2) return;
        var width = rows[0].Count;
        if (width < 2) return;

        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == width - 1)
            {
                row.Insert(BestMissingCellIndex(rows[0], row), "");
            }
        }
    }

    private static int BestMissingCellIndex(IReadOnlyList<string> header, IReadOnlyList<string> row)
    {
        var width = header.Count;
        var bestIndex = width - 1;
        var bestScore = int.MinValue;
        for (var missing = 0; missing < width; missing++)
        {
            var score = 0;
            for (var col = 0; col < width; col++)
            {
                if (col == missing) continue;
                var sourceIndex = col < missing ? col : col - 1;
                score += HeaderCellAffinity(header[col], row[sourceIndex]);
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = missing;
            }
        }

        return bestIndex;
    }

    private static int HeaderCellAffinity(string header, string cell)
    {
        var h = header.Trim().ToLowerInvariant();
        var c = cell.Trim().ToLowerInvariant();
        if (h.Length == 0 || c.Length == 0) return 0;
        if (IsDateHeader(h) && LooksLikeDateCell(c)) return 4;
        if (IsStatusHeader(h) && LooksLikeStatusCell(c)) return 4;
        if (IsPriorityHeader(h) && LooksLikePriorityCell(c)) return 4;
        return 0;
    }

    private static bool IsDateHeader(string header)
        => header.Contains("日期", StringComparison.Ordinal)
           || header.Contains("截止", StringComparison.Ordinal)
           || header.Contains("date", StringComparison.Ordinal)
           || header.Contains("deadline", StringComparison.Ordinal)
           || header.Contains("due", StringComparison.Ordinal);

    private static bool IsStatusHeader(string header)
        => header.Contains("进度", StringComparison.Ordinal)
           || header.Contains("状态", StringComparison.Ordinal)
           || header.Contains("status", StringComparison.Ordinal)
           || header.Contains("progress", StringComparison.Ordinal);

    private static bool IsPriorityHeader(string header)
        => header.Contains("优先级", StringComparison.Ordinal)
           || header.Contains("priority", StringComparison.Ordinal);

    private static bool LooksLikeDateCell(string cell)
        => Regex.IsMatch(cell, @"^\d{4}[-/.]\d{1,2}[-/.]\d{1,2}$")
           || Regex.IsMatch(cell, @"^\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4}$");

    private static bool LooksLikeStatusCell(string cell)
        => cell is "未开始" or "进行中" or "已完成" or "完成" or "待办" or "暂停"
           || cell.Contains("done", StringComparison.Ordinal)
           || cell.Contains("todo", StringComparison.Ordinal)
           || cell.Contains("progress", StringComparison.Ordinal)
           || cell.Contains("pending", StringComparison.Ordinal);

    private static bool LooksLikePriorityCell(string cell)
        => cell is "高" or "中" or "低" or "紧急" or "普通"
           || cell.Contains("high", StringComparison.Ordinal)
           || cell.Contains("medium", StringComparison.Ordinal)
           || cell.Contains("low", StringComparison.Ordinal)
           || Regex.IsMatch(cell, @"^p[0-5]$");

    private static string EscapeMdCell(string s)
        => s.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
}

internal sealed class WordLookupAction : BuiltInAction
{
    private readonly IOfflineDictionaryService _dictionary;

    public WordLookupAction(IOfflineDictionaryService dictionary) => _dictionary = dictionary;

    public override string Id => BuiltInActionIds.WordLookup;
    public override string Title => "查词";
    public override string IconKey => SmartActionIcons.Dictionary;

    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty
           && _dictionary.IsAvailable
           && SmartTextProbes.LooksLikeEnglishLookup(context.Text);

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var query = SmartTextProbes.NormalizeEnglishLookup(context.Text);
        var results = _dictionary.Lookup(query, maxResults: 6);
        if (results.Count == 0)
        {
            host.Notifier.Notify($"词库未找到：{query}");
            return Task.CompletedTask;
        }

        var text = FormatDictionaryResults(query, results);
        SmartOutput.Publish(host, context, Title, text,
            copyToast: $"已查询 {query}（{results.Count} 条，已复制）",
            fallback: BuiltInOutputMode.Bubble);
        return Task.CompletedTask;
    }

    internal static string FormatDictionaryResults(string query, IReadOnlyList<DictionaryLookupResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine(query);
        sb.AppendLine();
        foreach (var item in results)
        {
            AppendDictionaryItem(sb, item);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    internal static void AppendDictionaryItem(StringBuilder sb, DictionaryLookupResult item)
    {
        sb.Append(item.Word);
        if (!string.IsNullOrWhiteSpace(item.MatchedFrom)
            && !string.Equals(item.Word, item.MatchedFrom, StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" ← ").Append(item.MatchedFrom);
        }
        if (!string.IsNullOrWhiteSpace(item.Phonetic)) sb.Append(" /").Append(item.Phonetic).Append('/');
        if (!string.IsNullOrWhiteSpace(item.PartOfSpeech)) sb.Append("  ").Append(item.PartOfSpeech);
        sb.AppendLine();

        var level = VocabularyAnalyzeAction.EstimateLevel(item);
        sb.AppendLine($"难度: {level.Label}");
        if (!string.IsNullOrWhiteSpace(item.Tags)) sb.AppendLine("标签: " + item.Tags);
        if (!string.IsNullOrWhiteSpace(item.Translation))
        {
            sb.AppendLine(NormalizeMultiline("释义: " + FilterDictionaryLines(item.Translation, dropComputerDefinitions: true)));
        }
        if (!string.IsNullOrWhiteSpace(item.Definition))
        {
            sb.AppendLine(NormalizeMultiline("EN: " + item.Definition));
        }
        if (!string.IsNullOrWhiteSpace(item.Exchange))
        {
            sb.AppendLine("变形: " + item.Exchange);
        }
    }

    private static string NormalizeMultiline(string text)
        => text.Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static string FilterDictionaryLines(string text, bool dropComputerDefinitions)
    {
        var normalized = text.Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Where(line => !(dropComputerDefinitions && line.Contains("[计]", StringComparison.Ordinal)))
            .ToArray();
        return string.Join('\n', lines);
    }
}

internal sealed class VocabularyAnalyzeAction : BuiltInAction
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "if", "then", "else", "for", "to", "of", "in", "on", "at", "by",
        "with", "from", "as", "is", "are", "was", "were", "be", "been", "being", "am", "this", "that", "these",
        "those", "it", "its", "they", "them", "their", "we", "our", "you", "your", "he", "she", "his", "her",
        "i", "me", "my", "do", "does", "did", "done", "have", "has", "had", "will", "would", "can", "could",
        "should", "may", "might", "not", "no", "yes", "so", "very", "just", "also", "than", "too",
    };

    private readonly IOfflineDictionaryService _dictionary;

    public VocabularyAnalyzeAction(IOfflineDictionaryService dictionary) => _dictionary = dictionary;

    public override string Id => BuiltInActionIds.VocabularyAnalyze;
    public override string Title => "词汇解析";
    public override string IconKey => SmartActionIcons.Vocabulary;

    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty
           && _dictionary.IsAvailable
           && SmartTextProbes.LooksLikeEnglishText(context.Text)
           && ExtractCandidates(context.Text, maxCandidates: 3).Count > 0;

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var candidates = ExtractCandidates(context.Text, maxCandidates: 80);
        var scored = new List<(DictionaryLookupResult Entry, int Score)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in candidates)
        {
            var hit = _dictionary.Lookup(word, maxResults: 1).FirstOrDefault();
            if (hit is null || !seen.Add(hit.Word)) continue;
            if (HasComputerDefinition(hit)) continue;
            if (HasBasicSchoolTag(hit)) continue;
            var score = ScoreVocabularyItem(hit);
            if (score < 10) continue;
            scored.Add((hit, score));
        }

        var entries = scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Word.Length)
            .Take(10)
            .Select(x => x.Entry)
            .ToList();

        if (entries.Count == 0)
        {
            host.Notifier.Notify("词库未找到可解析词汇");
            return Task.CompletedTask;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"词汇解析（{entries.Count} 个）");
        sb.AppendLine();
        foreach (var item in entries)
        {
            WordLookupAction.AppendDictionaryItem(sb, item);
            sb.AppendLine();
        }

        var text = sb.ToString().TrimEnd();
        SmartOutput.Publish(host, context, Title, text,
            copyToast: $"已解析 {entries.Count} 个词（已复制）",
            fallback: BuiltInOutputMode.Bubble);
        return Task.CompletedTask;
    }

    internal static (string Label, int Rank) EstimateLevel(DictionaryLookupResult item)
    {
        var score = ScoreVocabularyItem(item);
        if (score >= 35) return ("高级", 3);
        if (score >= 10) return ("进阶", 2);
        return ("基础", 1);
    }

    private static int ScoreVocabularyItem(DictionaryLookupResult item)
    {
        var word = item.Word;
        var tags = " " + (item.Tags ?? "").ToLowerInvariant() + " ";
        var score = 0;

        if (tags.Contains(" gre ", StringComparison.Ordinal)) score += 35;
        if (tags.Contains(" toefl ", StringComparison.Ordinal)) score += 22;
        if (tags.Contains(" ielts ", StringComparison.Ordinal)) score += 20;
        if (tags.Contains(" ky ", StringComparison.Ordinal)) score += 18;
        if (tags.Contains(" cet6 ", StringComparison.Ordinal)) score += 12;
        if (tags.Contains(" cet4 ", StringComparison.Ordinal)) score += 4;
        if (tags.Contains(" zk ", StringComparison.Ordinal) || tags.Contains(" gk ", StringComparison.Ordinal)) score -= 18;

        score += item.Collins switch
        {
            null => 14,
            <= 1 => 22,
            2 => 14,
            3 => 4,
            >= 4 => -16,
        };

        var rank = item.Bnc ?? item.Frq;
        if (rank is null) score += 8;
        else if (rank > 20000) score += 30;
        else if (rank > 8000) score += 22;
        else if (rank > 3000) score += 12;
        else if (rank < 1000) score -= 22;
        else if (rank < 3000) score -= 10;

        if (word.Contains('-', StringComparison.Ordinal)) score += 14;
        if (word.Length >= 12) score += 12;
        else if (word.Length >= 9) score += 7;
        if (StopWords.Contains(word)) score -= 50;

        return score;
    }

    private static bool HasComputerDefinition(DictionaryLookupResult item)
        => (item.Translation ?? "").Contains("[计]", StringComparison.Ordinal);

    private static bool HasBasicSchoolTag(DictionaryLookupResult item)
    {
        var tags = " " + (item.Tags ?? "").ToLowerInvariant() + " ";
        return tags.Contains(" zk ", StringComparison.Ordinal)
            || tags.Contains(" gk ", StringComparison.Ordinal);
    }

    private static List<string> ExtractCandidates(string text, int maxCandidates)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, @"[A-Za-z][A-Za-z'-]{2,}"))
        {
            var word = match.Value.Trim('\'', '-');
            if (word.Length < 4 || StopWords.Contains(word)) continue;
            if (word.All(char.IsUpper) && word.Length <= 5) continue;
            if (!seen.Add(word)) continue;
            list.Add(word);
            if (list.Count >= maxCandidates) break;
        }
        return list;
    }
}

/// <summary>用于多个 SmartAction 共享的"轻量探测"。
/// 关键约束：每个方法都必须在亚毫秒内返回 —— 浮窗弹出前会全量调一遍，重 IO 会拖慢整体响应</summary>
internal static class SmartTextProbes
{
    /// <summary>用 trim 后首末字符快速过滤大多数非 JSON 文本；
    /// 通过后再 TryParse 做完整验证。短字符串（&lt;2 字符）直接拒绝</summary>
    public static bool LooksLikeJson(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.AsSpan().Trim();
        if (t.Length < 2) return false;
        var first = t[0];
        var last = t[^1];
        if (!((first == '{' && last == '}') || (first == '[' && last == ']'))) return false;
        try
        {
            using var doc = JsonDocument.Parse(t.ToString());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool LooksLikeEnglishLookup(string text)
    {
        var normalized = NormalizeEnglishLookup(text);
        if (normalized.Length < 2 || normalized.Length > 80) return false;
        if (normalized.Count(char.IsWhiteSpace) > 3) return false;
        return Regex.IsMatch(normalized, @"^[A-Za-z][A-Za-z'\-]*(?:\s+[A-Za-z][A-Za-z'\-]*){0,3}$");
    }

    public static bool LooksLikeEnglishText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 12) return false;
        var letters = text.Count(char.IsAsciiLetter);
        if (letters < 8) return false;
        return (double)letters / Math.Max(1, text.Count(c => !char.IsWhiteSpace(c))) >= 0.55;
    }

    public static string NormalizeEnglishLookup(string text)
    {
        var normalized = Regex.Replace(text.Trim(), @"^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$", "");
        return Regex.Replace(normalized, @"\s+", " ");
    }

    /// <summary>识别 Markdown 表格的最低条件：
    /// 至少 2 行非空 + 全部以 | 开头结尾 + 至少出现一行"对齐分隔行" |---|---|</summary>
    public static bool LooksLikeMarkdownTable(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var lines = text.Replace("\r\n", "\n")
                        .Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .ToArray();
        if (lines.Length < 2) return false;
        var hasSeparator = false;
        foreach (var line in lines)
        {
            if (!line.StartsWith('|') || !line.EndsWith('|')) return false;
            if (Regex.IsMatch(line, @"^\|(\s*:?-+:?\s*\|)+$"))
            {
                hasSeparator = true;
            }
        }
        return hasSeparator;
    }

    /// <summary>识别 CSV：≥2 行非空，表头至少 2 列，数据行允许完整或少 1 个字段。
    /// 少 1 个字段用于覆盖 OCR / 手写 CSV 常见的漏空单元格场景；更残缺的文本仍拒绝，避免普通段落误触发。</summary>
    public static bool LooksLikeCsv(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var lines = text.Replace("\r\n", "\n")
                        .Split('\n')
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToArray();
        if (lines.Length < 2) return false;
        var firstCommas = lines[0].Count(c => c == ',');
        if (firstCommas < 2) return false;
        for (var i = 1; i < lines.Length; i++)
        {
            var commas = lines[i].Count(c => c == ',');
            if (commas != firstCommas && commas != firstCommas - 1) return false;
        }
        return true;
    }

    /// <summary>识别 TSV：≥2 行非空，每行包含至少 1 个 \t，且各行 \t 数量一致。
    /// 与 CSV 探测对称，但分隔符换成 Tab。常见来源：从 Excel / Numbers / 网页表格直接复制的内容</summary>
    public static bool LooksLikeTsv(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.IndexOf('\t') < 0) return false;
        var lines = text.Replace("\r\n", "\n")
                        .Split('\n')
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToArray();
        if (lines.Length < 2) return false;
        var firstTabs = lines[0].Count(c => c == '\t');
        if (firstTabs < 1) return false;
        foreach (var line in lines)
        {
            if (line.Count(c => c == '\t') != firstTabs) return false;
        }
        return true;
    }
}

/// <summary>TSV → CSV。识别条件同 LooksLikeTsv；输出标准 CSV（含逗号/引号/换行的单元格自动加引号）。
/// 与 MarkdownTableToCsv 共用 QuoteCsvCell 的逻辑思路，但因跨文件不共享 helper，本类内独立实现一份</summary>
internal sealed class TsvToCsvAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.TsvToCsv;
    public override string Title => "TSV → CSV";
    public override string IconKey => SmartActionIcons.TsvToCsv;

    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty && SmartTextProbes.LooksLikeTsv(context.Text);

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var rows = ParseTsv(context.Text);
        if (rows.Count == 0)
        {
            host.Notifier.Notify("未识别到 TSV 数据");
            return Task.CompletedTask;
        }
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(QuoteCsvCell(row[i]));
            }
            sb.Append('\n');
        }
        var csv = sb.ToString().TrimEnd('\n');
        SmartOutput.Publish(host, context, Title, csv,
            copyToast: $"已转为 CSV（{rows.Count} 行 × {rows[0].Count} 列，已复制）");
        return Task.CompletedTask;
    }

    private static List<List<string>> ParseTsv(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var rows = new List<List<string>>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            rows.Add(line.Split('\t').ToList());
        }
        return rows;
    }

    private static string QuoteCsvCell(string cell)
    {
        if (cell.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return cell;
        return "\"" + cell.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>TSV → Markdown 表格。与 CsvToMarkdown 输出相同形态，仅源解析按 \t 切分。
/// 不要求 TSV 第一行是 header：直接把第一行当列名 + 分隔行，余下都是数据</summary>
internal sealed class TsvToMarkdownAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.TsvToMarkdown;
    public override string Title => "TSV → MD 表格";
    public override string IconKey => SmartActionIcons.TsvToMd;

    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty && SmartTextProbes.LooksLikeTsv(context.Text);

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var rows = ParseTsv(context.Text);
        if (rows.Count < 2)
        {
            host.Notifier.Notify("TSV 行数不足，无法构造表格");
            return Task.CompletedTask;
        }
        var sb = new StringBuilder();
        var width = rows[0].Count;
        sb.Append('|');
        foreach (var c in rows[0]) sb.Append(' ').Append(EscapeMdCell(c)).Append(" |");
        sb.Append('\n').Append('|');
        for (var i = 0; i < width; i++) sb.Append(" --- |");
        sb.Append('\n');
        for (var r = 1; r < rows.Count; r++)
        {
            sb.Append('|');
            for (var i = 0; i < width; i++)
            {
                var cell = i < rows[r].Count ? rows[r][i] : "";
                sb.Append(' ').Append(EscapeMdCell(cell)).Append(" |");
            }
            sb.Append('\n');
        }
        var md = sb.ToString().TrimEnd('\n');
        SmartOutput.Publish(host, context, Title, md,
            copyToast: $"已转为 Markdown 表格（{rows.Count} 行 × {width} 列，已复制）");
        return Task.CompletedTask;
    }

    private static List<List<string>> ParseTsv(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var rows = new List<List<string>>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            rows.Add(line.Split('\t').ToList());
        }
        return rows;
    }

    private static string EscapeMdCell(string s)
        => s.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
}

/// <summary>把 JsonElement 写成 YAML 文本。
/// 设计为"够用即可"：标量 / 数组 / 对象 / null 全部支持；
/// 含特殊字符（: # & * 等）的字符串自动加引号，避免破坏 YAML 语法</summary>
internal static class JsonToYamlWriter
{
    private const string IndentUnit = "  ";

    public static void Write(JsonElement element, StringBuilder sb, int indentLevel, bool isRoot)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, sb, indentLevel, isRoot);
                break;
            case JsonValueKind.Array:
                WriteArray(element, sb, indentLevel, isRoot);
                break;
            default:
                sb.Append(FormatScalar(element));
                sb.Append('\n');
                break;
        }
    }

    private static void WriteObject(JsonElement obj, StringBuilder sb, int indentLevel, bool isRoot)
    {
        var first = true;
        foreach (var prop in obj.EnumerateObject())
        {
            if (!isRoot || !first) sb.Append(Indent(indentLevel));
            sb.Append(FormatKey(prop.Name));
            sb.Append(':');
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                // 子结构换行 + 缩进，与 YAML 习惯一致
                sb.Append('\n');
                Write(prop.Value, sb, indentLevel + 1, isRoot: false);
            }
            else
            {
                sb.Append(' ').Append(FormatScalar(prop.Value)).Append('\n');
            }
            first = false;
        }
    }

    private static void WriteArray(JsonElement arr, StringBuilder sb, int indentLevel, bool isRoot)
    {
        foreach (var item in arr.EnumerateArray())
        {
            sb.Append(Indent(indentLevel));
            sb.Append("- ");
            if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                sb.Append('\n');
                Write(item, sb, indentLevel + 1, isRoot: false);
            }
            else
            {
                sb.Append(FormatScalar(item)).Append('\n');
            }
        }
    }

    private static string Indent(int level) => string.Concat(Enumerable.Repeat(IndentUnit, level));

    private static string FormatKey(string name)
        => RequiresQuoting(name) ? Quote(name) : name;

    private static string FormatScalar(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => RequiresQuoting(el.GetString() ?? "") ? Quote(el.GetString()!) : el.GetString() ?? "",
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => "",
    };

    /// <summary>判断字符串能否裸写在 YAML 里：含特殊语义字符、首字符为引号/连字符等都需要加引号</summary>
    private static bool RequiresQuoting(string s)
    {
        if (s.Length == 0) return true;
        if (s.IndexOfAny(new[] { ':', '#', '&', '*', '!', '|', '>', '\'', '"', '%', '@', '`', '\n', '\r', '\t' }) >= 0) return true;
        if (s.Trim() != s) return true;
        if (s[0] is '-' or '?' or ',' or '[' or ']' or '{' or '}') return true;
        // 防止被解释为 bool / null
        if (string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "null", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Quote(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
}

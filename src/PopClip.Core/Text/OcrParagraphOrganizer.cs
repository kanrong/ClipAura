using System.Text;

namespace PopClip.Core.Text;

/// <summary>
/// Deterministic OCR paragraph cleanup. It only changes whitespace layout:
/// suspicious hard line breaks become spaces (or nothing for CJK joins), while
/// blank lines, headings, lists and sentence boundaries stay as paragraph breaks.
/// </summary>
public static class OcrParagraphOrganizer
{
    public static bool CanImprove(string? text)
    {
        var normalized = NormalizeNewlines(text).Trim();
        if (normalized.Length == 0 || !normalized.Contains('\n')) return false;
        return !string.Equals(normalized, Organize(text), StringComparison.Ordinal);
    }

    public static string Organize(string? text)
    {
        var normalized = NormalizeNewlines(text);
        if (string.IsNullOrWhiteSpace(normalized)) return "";

        var lines = normalized.Split('\n');
        var output = new List<string>();
        var current = "";

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                FlushCurrent(output, ref current);
                AddParagraphBreak(output);
                continue;
            }

            if (current.Length == 0)
            {
                current = line;
                continue;
            }

            if (ShouldMerge(current, line))
            {
                current += Joiner(current, line) + line;
            }
            else
            {
                FlushCurrent(output, ref current);
                current = line;
            }
        }

        FlushCurrent(output, ref current);
        TrimTrailingBreaks(output);

        var organized = string.Join('\n', output).Trim();
        return HasSameNonWhitespaceContent(normalized, organized)
            ? organized
            : normalized.Trim();
    }

    private static string NormalizeNewlines(string? text)
        => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    private static void FlushCurrent(List<string> output, ref string current)
    {
        if (current.Length == 0) return;
        output.Add(current);
        current = "";
    }

    private static void AddParagraphBreak(List<string> output)
    {
        if (output.Count == 0) return;
        if (output[^1].Length == 0) return;
        output.Add("");
    }

    private static void TrimTrailingBreaks(List<string> output)
    {
        while (output.Count > 0 && output[^1].Length == 0)
        {
            output.RemoveAt(output.Count - 1);
        }
    }

    private static bool ShouldMerge(string previous, string next)
    {
        if (LooksLikeListItem(previous) || LooksLikeListItem(next)) return false;
        if (LooksLikeTableRow(previous) || LooksLikeTableRow(next)) return false;
        if (LooksLikeStandaloneHeading(previous, next)) return false;
        if (EndsWithBoundary(previous)) return false;
        if (StartsWithBoundary(next)) return false;
        return true;
    }

    private static bool LooksLikeListItem(string line)
    {
        var t = line.TrimStart();
        if (t.Length == 0) return false;
        if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal)) return true;
        if (t.Length >= 2 && (t[0] is '•' or '·') && char.IsWhiteSpace(t[1])) return true;
        var i = 0;
        while (i < t.Length && char.IsDigit(t[i])) i++;
        return i > 0 && i + 1 < t.Length && (t[i] is '.' or ')' or '、') && char.IsWhiteSpace(t[i + 1]);
    }

    private static bool LooksLikeTableRow(string line)
        => line.Count(c => c == '|') >= 2 || line.Count(c => c == '\t') >= 1;

    private static bool LooksLikeStandaloneHeading(string previous, string next)
    {
        var p = previous.Trim();
        var n = next.Trim();
        if (p.Length == 0 || n.Length == 0) return false;
        if (p.Length > 24) return false;
        if (p.IndexOfAny(new[] { ',', '，', ';', '；', '、' }) >= 0) return false;
        if (EndsWithBoundary(p)) return true;
        return n.Length > p.Length * 2 && !char.IsLower(n[0]);
    }

    private static bool EndsWithBoundary(string line)
    {
        var ch = LastNonWhitespace(line);
        return ch is '.' or '?' or '!' or ':' or '。' or '？' or '！' or '：' or '…';
    }

    private static bool StartsWithBoundary(string line)
    {
        var ch = FirstNonWhitespace(line);
        return ch is ')' or ']' or '}' or '）' or '】' or '》' or ',' or '.' or ';' or ':' or '?' or '!'
            or '，' or '。' or '；' or '：' or '？' or '！' or '、';
    }

    private static string Joiner(string previous, string next)
    {
        var prev = LastNonWhitespace(previous);
        var first = FirstNonWhitespace(next);
        if (prev == '\0' || first == '\0') return "";
        if (prev is '-' or '/' or '\\' or '(' or '[' or '{' or '（' or '【' or '《') return "";
        if (StartsWithBoundary(next)) return "";
        return IsCjk(prev) || IsCjk(first) ? "" : " ";
    }

    private static char LastNonWhitespace(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i])) return text[i];
        }
        return '\0';
    }

    private static char FirstNonWhitespace(string text)
    {
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch)) return ch;
        }
        return '\0';
    }

    private static bool IsCjk(char ch)
        => ch is >= '\u3400' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF'
            or >= '\u3040' and <= '\u30FF'
            or >= '\uAC00' and <= '\uD7AF';

    private static bool HasSameNonWhitespaceContent(string left, string right)
        => string.Equals(CompactWhitespace(left), CompactWhitespace(right), StringComparison.Ordinal);

    private static string CompactWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }
}

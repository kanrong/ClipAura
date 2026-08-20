using System.Text;

namespace PopClip.Actions.BuiltIn;

/// <summary>本地「整理格式」。只动空白与换行，四条硬规则：
/// 1. 行内连续半角空格/tab → 1 个空格；连续全角空格 → 1 个全角空格
/// 2. 连续 ≥2 个换行 → 1 个换行（段间空行去掉）
/// 3. 行末空白去掉
/// 4. 行末是 , ，、 → 删掉随后换行并与下一行拼接
/// ` ``` ` / `~~~` 围栏内原样不动。</summary>
internal static class TextTidy
{
#if DEBUG
    static TextTidy() => SelfCheck();
#endif

    public static bool NeedsTidy(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return Run(text, dst: null);
    }

    public static string Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        Run(text, sb);
        return sb.ToString();
    }

    private static bool Run(string text, StringBuilder? dst)
    {
        var newline = text.AsSpan().IndexOf("\r\n") >= 0 ? "\r\n" : "\n";
        var lines = SplitLines(text);
        var probe = dst is null ? new DiffProbe(text) : null;
        var emitter = new Emitter(dst, probe);

        var pieces = BuildPieces(text, lines);
        for (var i = 0; i < pieces.Count; i++)
        {
            if (i > 0) emitter.Emit(newline);
            var s = pieces[i].Text;
            if (s.Length > 0) emitter.Emit(s);
            if (probe is { AlreadyDifferent: true }) return true;
        }

        if (pieces.Count > 0 && lines.EndedWithNewline) emitter.Emit(newline);
        return probe is null ? !emitter.MatchesOriginal(text) : probe.DifferentOrShorter();
    }

    private static List<Piece> BuildPieces(string text, LineList lines)
    {
        var pieces = new List<Piece>(lines.Count);
        var inFence = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i].Span(text);
            if (IsFenceMarker(raw) || inFence)
            {
                if (IsFenceMarker(raw)) inFence = !inFence;
                pieces.Add(new Piece(true, raw.ToString()));
                continue;
            }

            var cleaned = CleanLine(raw);
            if (cleaned.Length == 0) continue;

            if (pieces.Count > 0 && !pieces[^1].Fence && EndsWithComma(pieces[^1].Text))
            {
                pieces[^1] = new Piece(false, JoinComma(pieces[^1].Text, cleaned));
                continue;
            }

            pieces.Add(new Piece(false, cleaned));
        }

        return pieces;
    }

    private readonly struct Piece(bool fence, string text)
    {
        public bool Fence { get; } = fence;
        public string Text { get; } = text;
    }

    private static string CleanLine(ReadOnlySpan<char> raw)
    {
        if (raw.Length == 0) return "";
        var sb = new StringBuilder(raw.Length);
        var asciiWs = false;
        var fullWs = false;
        foreach (var ch in raw)
        {
            if (ch is ' ' or '\t')
            {
                if (asciiWs) continue;
                sb.Append(' ');
                asciiWs = true;
                fullWs = false;
                continue;
            }
            if (ch == '\u3000')
            {
                if (fullWs) continue;
                sb.Append('\u3000');
                fullWs = true;
                asciiWs = false;
                continue;
            }
            sb.Append(ch);
            asciiWs = false;
            fullWs = false;
        }

        while (sb.Length > 0 && IsHorizontalWs(sb[^1])) sb.Length--;
        return sb.ToString();
    }

    private static string JoinComma(string left, string right)
    {
        var r = right.AsSpan();
        var i = 0;
        while (i < r.Length && IsHorizontalWs(r[i])) i++;
        r = r[i..];
        if (r.Length == 0) return left;
        if (NeedsJoinSpace(r[0])) return left + " " + r.ToString();
        return left + r.ToString();
    }

    private static bool NeedsJoinSpace(char first)
        => first is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static bool EndsWithComma(string s)
        => s.Length > 0 && s[^1] is ',' or '，' or '、';

    private static bool IsHorizontalWs(char ch) => ch is ' ' or '\t' or '\u3000';

    private static bool IsFenceMarker(ReadOnlySpan<char> raw)
    {
        var i = 0;
        while (i < raw.Length && i < 3 && raw[i] == ' ') i++;
        if (i >= raw.Length) return false;
        var ch = raw[i];
        if (ch is not '`' and not '~') return false;
        var n = 0;
        while (i < raw.Length && raw[i] == ch) { n++; i++; }
        return n >= 3;
    }

    private static LineList SplitLines(string text)
    {
        var lines = new List<LineRange>(16);
        var i = 0;
        var endedWithNewline = false;
        while (i < text.Length)
        {
            var start = i;
            while (i < text.Length && text[i] is not '\n' and not '\r') i++;
            lines.Add(new LineRange(start, i - start));
            if (i >= text.Length)
            {
                endedWithNewline = false;
                break;
            }
            endedWithNewline = true;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i += 2;
            else i++;
        }
        return new LineList(lines, endedWithNewline);
    }

    private readonly struct LineRange(int start, int length)
    {
        public ReadOnlySpan<char> Span(string text) => text.AsSpan(start, length);
    }

    private sealed class LineList(List<LineRange> items, bool endedWithNewline)
    {
        public int Count => items.Count;
        public bool EndedWithNewline { get; } = endedWithNewline;
        public LineRange this[int index] => items[index];
    }

    private sealed class DiffProbe(string source)
    {
        private int _i;
        private bool _different;

        public void Write(ReadOnlySpan<char> s)
        {
            if (_different) return;
            if (_i + s.Length > source.Length || !source.AsSpan(_i, s.Length).SequenceEqual(s))
            {
                _different = true;
                return;
            }
            _i += s.Length;
        }

        public bool AlreadyDifferent => _different;
        public bool DifferentOrShorter() => _different || _i != source.Length;
    }

    private sealed class Emitter(StringBuilder? dst, DiffProbe? probe)
    {
        public void Emit(ReadOnlySpan<char> s)
        {
            if (s.Length == 0) return;
            probe?.Write(s);
            dst?.Append(s);
        }

        public void Emit(string s)
        {
            if (s.Length == 0) return;
            probe?.Write(s.AsSpan());
            dst?.Append(s);
        }

        public bool MatchesOriginal(string text)
            => dst is not null && dst.Length == text.Length && dst.ToString() == text;
    }

#if DEBUG
    private static void SelfCheck()
    {
        Case("clean", "hello", "hello", false);
        Case("posix", "hello\n", "hello\n", false);
        Case("crlf", "hello\r\nworld\r\n", "hello\r\nworld\r\n", false);
        Case("spaces", "a  b   c", "a b c", true);
        Case("tabs", "a\tb\t\tc", "a b c", true);
        Case("fullwidth", "a　　b", "a　b", true);
        Case("trail", "hello  \nworld", "hello\nworld", true);
        Case("blanks", "a\n\n\nb", "a\nb", true);
        Case("comma-en", "foo,\nbar", "foo, bar", true);
        Case("comma-zh", "你好，\n世界", "你好，世界", true);
        Case("enum", "苹果、\n香蕉", "苹果、香蕉", true);
        Case("comma-blank", "foo,\n\nbar", "foo, bar", true);
        Case("no-wrap", "that was\nbroken", "that was\nbroken", false);
        Case("fence", "```\nfoo  bar\n\n\nbaz\n```", "```\nfoo  bar\n\n\nbaz\n```", false);
        Case("chain", "a,\nb,\nc", "a, b, c", true);
    }

    private static void Case(string name, string input, string expected, bool expectNeed)
    {
        var got = Apply(input);
        if (!string.Equals(got, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"TextTidy[{name}] apply: got {Show(got)}, expected {Show(expected)}");
        var need = NeedsTidy(input);
        if (need != expectNeed)
            throw new InvalidOperationException($"TextTidy[{name}] need: got {need}, expected {expectNeed}");
        if (need != !string.Equals(got, input, StringComparison.Ordinal))
            throw new InvalidOperationException($"TextTidy[{name}] need/apply mismatch");
    }

    private static string Show(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");
#endif
}

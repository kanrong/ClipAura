using System.Text;
using PopClip.App.Ocr;

namespace PopClip.Ocr.Layout;

public static class OcrLayoutAnalyzer
{
    public static OcrLayoutResult Analyze(OcrResult result, OcrLayoutOptions? options = null)
    {
        options ??= new OcrLayoutOptions();
        if (result.Blocks.Count == 0) return OcrLayoutResult.Empty;

        var tokens = result.Blocks
            .Select((block, index) => Token.FromBlock(block, index))
            .Where(t => t.Text.Length > 0 && t.Bounds.Width > 0 && t.Bounds.Height > 0)
            .ToList();
        if (tokens.Count == 0) return OcrLayoutResult.Empty;

        var medianHeight = Math.Max(1f, Median(tokens.Select(t => t.Bounds.Height)));
        var rows = BuildRows(tokens, medianHeight, options);

        var consumed = new HashSet<int>();
        var regions = new List<OcrLayoutRegion>();

        foreach (var tableRows in DetectTableRows(rows, medianHeight, options))
        {
            var tableTokens = tableRows.SelectMany(r => r.Tokens).ToList();
            if (tableTokens.Any(t => consumed.Contains(t.Index))) continue;
            foreach (var t in tableTokens) consumed.Add(t.Index);
            regions.Add(BuildTableRegion(tableRows));
        }

        foreach (var component in BuildTextComponents(tokens.Where(t => !consumed.Contains(t.Index)).ToList(), medianHeight, options))
        {
            var region = BuildParagraphRegion(component, medianHeight, options);
            if (region.Text.Length > 0) regions.Add(region);
        }

        regions.Sort(CompareRegionsReadingOrder);
        var plainText = string.Join("\n\n", regions.Select(r => r.Text).Where(t => t.Length > 0)).Trim();
        return new OcrLayoutResult(regions, plainText);
    }

    private static List<Row> BuildRows(IReadOnlyList<Token> tokens, float medianHeight, OcrLayoutOptions options)
    {
        var rows = new List<Row>();
        foreach (var token in tokens.OrderBy(t => t.Bounds.CenterY).ThenBy(t => t.Bounds.Left))
        {
            Row? best = null;
            foreach (var row in rows)
            {
                var centerTolerance = Math.Max(4f, Math.Min(row.Bounds.Height, token.Bounds.Height) * options.SameLineCenterYToleranceRatio);
                var centerClose = Math.Abs(row.Bounds.CenterY - token.Bounds.CenterY) <= centerTolerance;
                var overlap = row.Bounds.VerticalOverlapRatio(token.Bounds) >= options.SameLineMinYOverlapRatio;
                if (centerClose || overlap)
                {
                    best = row;
                    break;
                }
            }

            if (best is null)
            {
                rows.Add(new Row(new List<Token> { token }));
            }
            else
            {
                best.Tokens.Add(token);
                best.RefreshBounds();
            }
        }

        foreach (var row in rows)
        {
            row.Tokens.Sort(CompareTokensReadingOrder);
            row.RefreshBounds();
        }

        rows.Sort(CompareRowsReadingOrder);
        return rows;
    }

    private static IEnumerable<IReadOnlyList<Row>> DetectTableRows(IReadOnlyList<Row> rows, float medianHeight, OcrLayoutOptions options)
    {
        var group = new List<Row>();
        Row? previous = null;

        foreach (var row in rows)
        {
            var qualifies = row.Tokens.Count >= 2;
            var verticalGap = previous is null ? 0 : previous.Bounds.VerticalGap(row.Bounds);
            if (!qualifies || (previous is not null && verticalGap > medianHeight * 2.4f))
            {
                if (IsTableGroup(group, medianHeight, options)) yield return group.ToArray();
                group.Clear();
            }

            if (qualifies) group.Add(row);
            previous = row;
        }

        if (IsTableGroup(group, medianHeight, options)) yield return group.ToArray();
    }

    private static bool IsTableGroup(IReadOnlyList<Row> rows, float medianHeight, OcrLayoutOptions options)
    {
        if (rows.Count < options.MinTableRows) return false;

        var centers = rows.SelectMany(r => r.Tokens.Select(t => t.Bounds.CenterX)).OrderBy(x => x).ToList();
        var columns = ClusterColumns(centers, Math.Max(4f, medianHeight * options.TableColumnXToleranceRatio));
        var stableColumns = columns.Count(c => rows.Count(r => r.Tokens.Any(t => Math.Abs(t.Bounds.CenterX - c.Center) <= c.Tolerance)) >= Math.Max(2, (int)Math.Ceiling(rows.Count * 0.6)));
        if (stableColumns < 2) return false;

        var fillRatios = new List<float>();
        var gaps = new List<float>();
        var tokenWidths = new List<float>();
        foreach (var row in rows)
        {
            var sorted = row.Tokens.OrderBy(t => t.Bounds.Left).ToList();
            var span = Math.Max(1f, sorted[^1].Bounds.Right - sorted[0].Bounds.Left);
            fillRatios.Add(sorted.Sum(t => t.Bounds.Width) / span);
            tokenWidths.AddRange(sorted.Select(t => t.Bounds.Width));

            for (var i = 1; i < sorted.Count; i++)
            {
                var gap = sorted[i].Bounds.Left - sorted[i - 1].Bounds.Right;
                if (gap > 0) gaps.Add(gap);
            }
        }

        var medianFill = Median(fillRatios);
        if (medianFill < options.MinTableRowFillRatio) return false;

        var medianGap = gaps.Count == 0 ? 0 : Median(gaps);
        var medianTokenWidth = Math.Max(1f, Median(tokenWidths));
        var maxReasonableGap = Math.Max(
            medianTokenWidth * options.TableMaxGapToTokenWidthRatio,
            medianHeight * options.TableMaxGapToLineHeightRatio);
        return medianGap <= maxReasonableGap;
    }

    private static IReadOnlyList<ColumnCluster> ClusterColumns(IReadOnlyList<float> centers, float tolerance)
    {
        var clusters = new List<ColumnCluster>();
        foreach (var x in centers)
        {
            var cluster = clusters.FirstOrDefault(c => Math.Abs(c.Center - x) <= tolerance);
            if (cluster is null)
            {
                clusters.Add(new ColumnCluster(x, tolerance, 1));
            }
            else
            {
                cluster.Center = ((cluster.Center * cluster.Count) + x) / (cluster.Count + 1);
                cluster.Count++;
            }
        }

        return clusters;
    }

    private static OcrLayoutRegion BuildTableRegion(IReadOnlyList<Row> rows)
    {
        var lines = new List<OcrLayoutLine>();
        foreach (var row in rows.OrderBy(r => r.Bounds.Top).ThenBy(r => r.Bounds.Left))
        {
            var cells = row.Tokens.OrderBy(t => t.Bounds.Left).Select(t => EscapeCsv(t.Text));
            var text = string.Join(',', cells);
            lines.Add(new OcrLayoutLine(row.Bounds, text, row.Tokens.Select(t => t.Index).ToArray()));
        }

        var bounds = OcrLayoutRect.Union(lines.Select(l => l.Bounds));
        return new OcrLayoutRegion(OcrLayoutContentKind.Table, bounds, lines, string.Join('\n', lines.Select(l => l.Text)).Trim());
    }

    private static IEnumerable<IReadOnlyList<Token>> BuildTextComponents(IReadOnlyList<Token> tokens, float medianHeight, OcrLayoutOptions options)
    {
        if (tokens.Count == 0) yield break;

        var parent = Enumerable.Range(0, tokens.Count).ToArray();
        for (var i = 0; i < tokens.Count; i++)
        {
            for (var j = i + 1; j < tokens.Count; j++)
            {
                if (ShouldConnect(tokens[i], tokens[j], medianHeight, options))
                {
                    Union(parent, i, j);
                }
            }
        }

        foreach (var group in tokens.Select((token, i) => (Root: Find(parent, i), Token: token)).GroupBy(x => x.Root))
        {
            var component = group.Select(x => x.Token).OrderBy(t => t.Bounds.Top).ThenBy(t => t.Bounds.Left).ToList();
            yield return component;
        }
    }

    private static bool ShouldConnect(Token a, Token b, float medianHeight, OcrLayoutOptions options)
    {
        var verticalGap = a.Bounds.VerticalGap(b.Bounds);
        if (verticalGap > medianHeight * options.RegionMaxVerticalGapRatio) return false;

        var horizontalOverlap = a.Bounds.HorizontalOverlapRatio(b.Bounds) > 0.08f;
        var horizontalGap = a.Bounds.HorizontalGap(b.Bounds);
        var horizontallyClose = horizontalGap <= medianHeight * options.RegionMaxHorizontalGapRatio;
        return horizontalOverlap || horizontallyClose;
    }

    private static OcrLayoutRegion BuildParagraphRegion(IReadOnlyList<Token> component, float medianHeight, OcrLayoutOptions options)
    {
        var rows = BuildRows(component, medianHeight, options);
        var lines = rows.Select(BuildParagraphLine).Where(l => l.Text.Length > 0).ToList();
        var bounds = OcrLayoutRect.Union(lines.Select(l => l.Bounds));
        var text = ComposeParagraphText(lines, bounds, options);
        return new OcrLayoutRegion(OcrLayoutContentKind.Paragraph, bounds, lines, text);
    }

    private static OcrLayoutLine BuildParagraphLine(Row row)
    {
        var sorted = row.Tokens.OrderBy(t => t.Bounds.Left).ToList();
        var sb = new StringBuilder();
        Token? previous = null;
        foreach (var token in sorted)
        {
            if (sb.Length > 0 && previous is not null)
            {
                sb.Append(InlineJoiner(previous.Value, token));
            }
            sb.Append(token.Text);
            previous = token;
        }

        return new OcrLayoutLine(row.Bounds, sb.ToString().Trim(), sorted.Select(t => t.Index).ToArray());
    }

    private static string ComposeParagraphText(IReadOnlyList<OcrLayoutLine> lines, OcrLayoutRect regionBounds, OcrLayoutOptions options)
    {
        if (lines.Count == 0) return "";

        var sb = new StringBuilder(lines[0].Text);
        var previousText = lines[0].Text;

        for (var i = 1; i < lines.Count; i++)
        {
            var nextText = lines[i].Text;
            if (ShouldMergeWrappedLine(lines[i - 1], lines[i], regionBounds, options))
            {
                var joiner = WrappedLineJoiner(previousText, nextText);
                if (joiner.RemoveTrailingHyphen && sb.Length > 0 && sb[^1] == '-')
                {
                    sb.Length--;
                }
                sb.Append(joiner.Text);
                sb.Append(nextText);
            }
            else
            {
                sb.Append('\n');
                sb.Append(nextText);
            }

            previousText = nextText;
        }

        return sb.ToString().Trim();
    }

    private static bool ShouldMergeWrappedLine(OcrLayoutLine previous, OcrLayoutLine next, OcrLayoutRect regionBounds, OcrLayoutOptions options)
    {
        var prevText = previous.Text.Trim();
        var nextText = next.Text.Trim();
        if (prevText.Length == 0 || nextText.Length == 0) return false;
        if (LooksLikeListItem(prevText) || LooksLikeListItem(nextText)) return false;
        if (EndsWithSentenceBoundary(prevText)) return false;
        if (LooksLikeStandaloneHeading(prevText, nextText)) return false;
        if (LooksLikeMetadataBoundary(previous, next, options)) return false;
        if (prevText.EndsWith("-", StringComparison.Ordinal) && IsAsciiLetterOrDigit(FirstNonWhitespace(nextText))) return true;

        var regionWidth = Math.Max(1f, regionBounds.Width);
        var fill = (previous.Bounds.Right - regionBounds.Left) / regionWidth;
        if (fill >= options.WrapFillRatio) return true;
        return char.IsLower(FirstNonWhitespace(nextText)) && fill >= options.LowercaseContinuationFillRatio;
    }

    private static bool LooksLikeMetadataBoundary(OcrLayoutLine previous, OcrLayoutLine next, OcrLayoutOptions options)
    {
        if (previous.Bounds.Height <= 0 || next.Bounds.Height <= 0) return false;

        var nextLooksLikeTags = next.Bounds.Height <= previous.Bounds.Height * options.MetadataLineMaxHeightRatio
            && LooksLikeMetadataLine(next, previous);
        if (nextLooksLikeTags) return true;

        var previousLooksLikeTags = previous.Bounds.Height <= next.Bounds.Height * options.MetadataLineMaxHeightRatio
            && LooksLikeMetadataLine(previous, next);
        return previousLooksLikeTags;
    }

    private static bool LooksLikeMetadataLine(OcrLayoutLine candidate, OcrLayoutLine neighbor)
    {
        if (candidate.SourceBlockIndexes.Count >= 2) return true;
        if (candidate.Text.Length <= 0 || neighbor.Text.Length <= 0) return false;
        return candidate.Bounds.Width < neighbor.Bounds.Width * 0.72f
            && candidate.Text.Length < neighbor.Text.Length * 0.85f;
    }

    private static string InlineJoiner(Token previous, Token next)
    {
        var prev = LastNonWhitespace(previous.Text);
        var first = FirstNonWhitespace(next.Text);
        if (prev == '\0' || first == '\0') return "";
        if (prev is '/' or '\\' or '(' or '[' or '{' or '（' or '【' or '《') return "";
        if (IsLeadingPunctuation(first)) return "";
        return " ";
    }

    private static (string Text, bool RemoveTrailingHyphen) WrappedLineJoiner(string previous, string next)
    {
        var prev = LastNonWhitespace(previous);
        var first = FirstNonWhitespace(next);
        if (prev == '\0' || first == '\0') return ("", false);
        if (prev == '-' && IsAsciiLetterOrDigit(first)) return ("", true);
        if (prev is '/' or '\\' or '(' or '[' or '{' or '（' or '【' or '《') return ("", false);
        if (IsLeadingPunctuation(first)) return ("", false);
        if (IsAsciiLetterOrDigit(prev) && IsAsciiLetterOrDigit(first)) return (" ", false);
        if (IsCjk(prev) || IsCjk(first)) return ("", false);
        return (" ", false);
    }

    private static bool EndsWithSentenceBoundary(string text)
    {
        var i = text.Length - 1;
        while (i >= 0 && (char.IsWhiteSpace(text[i]) || IsClosingPunctuation(text[i]))) i--;
        if (i < 0) return false;
        return text[i] is '.' or '?' or '!' or ':' or ';' or '。' or '？' or '！' or '：' or '；' or '…';
    }

    private static bool LooksLikeStandaloneHeading(string previous, string next)
    {
        var p = previous.Trim();
        var n = next.Trim();
        if (p.Length == 0 || n.Length == 0) return false;
        if (p.Length > 28) return false;
        if (p.IndexOfAny(new[] { ',', '，', ';', '；', '、' }) >= 0) return false;
        return n.Length > p.Length * 2 && !char.IsLower(FirstNonWhitespace(n));
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

    private static int CompareRegionsReadingOrder(OcrLayoutRegion left, OcrLayoutRegion right)
        => CompareRectsReadingOrder(left.Bounds, right.Bounds);

    private static int CompareRowsReadingOrder(Row left, Row right)
        => CompareRectsReadingOrder(left.Bounds, right.Bounds);

    private static int CompareTokensReadingOrder(Token left, Token right)
        => CompareRectsReadingOrder(left.Bounds, right.Bounds);

    private static int CompareRectsReadingOrder(OcrLayoutRect left, OcrLayoutRect right)
    {
        var h = Math.Max(1f, Math.Min(left.Height, right.Height));
        if (Math.Abs(left.CenterY - right.CenterY) > h * 0.65f)
            return left.Top.CompareTo(right.Top);
        return left.Left.CompareTo(right.Left);
    }

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.Where(v => !float.IsNaN(v) && !float.IsInfinity(v)).OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2f;
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

    private static bool IsAsciiLetterOrDigit(char ch)
        => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static bool IsCjk(char ch)
        => ch is >= '\u3400' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF'
            or >= '\u3040' and <= '\u30FF'
            or >= '\uAC00' and <= '\uD7AF';

    private static bool IsLeadingPunctuation(char ch)
        => ch is ')' or ']' or '}' or '）' or '】' or '》' or ',' or '.' or ';' or ':' or '?' or '!'
            or '，' or '。' or '；' or '：' or '？' or '！' or '、';

    private static bool IsClosingPunctuation(char ch)
        => ch is '"' or '\'' or ')' or ']' or '}' or '”' or '’' or '）' or '】' or '》';

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }
        return x;
    }

    private static void Union(int[] parent, int a, int b)
    {
        var ra = Find(parent, a);
        var rb = Find(parent, b);
        if (ra != rb) parent[rb] = ra;
    }

    private sealed class Row
    {
        public Row(List<Token> tokens)
        {
            Tokens = tokens;
            RefreshBounds();
        }

        public List<Token> Tokens { get; }
        public OcrLayoutRect Bounds { get; private set; }

        public void RefreshBounds() => Bounds = OcrLayoutRect.Union(Tokens.Select(t => t.Bounds));
    }

    private sealed class ColumnCluster
    {
        public ColumnCluster(float center, float tolerance, int count)
        {
            Center = center;
            Tolerance = tolerance;
            Count = count;
        }

        public float Center { get; set; }
        public float Tolerance { get; }
        public int Count { get; set; }
    }

    private readonly record struct Token(
        int Index,
        string Text,
        OcrLayoutRect Bounds)
    {
        public static Token FromBlock(OcrTextBlock block, int index)
            => new(index, block.Text.Trim(), OcrLayoutRect.FromPolygon(block.Box));
    }
}

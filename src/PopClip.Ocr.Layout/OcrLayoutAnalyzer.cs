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

        // 列表分类：用"行首 left 聚类 + 至少一行带列表标记"的几何信号判断是否成列表。
        // 即使该 component 不被整体判为 List，分类后的 LineKind 标记也会让下游
        // ComposeParagraphText 中"列表项不与前后行合并"的现有规则更稳。
        var (isList, classifiedLines) = ClassifyListLines(lines, medianHeight, options);

        if (isList)
        {
            var listText = ComposeListText(classifiedLines, medianHeight, options);
            return new OcrLayoutRegion(OcrLayoutContentKind.List, bounds, classifiedLines, listText);
        }

        var paragraphText = ComposeParagraphText(classifiedLines, bounds, medianHeight, options);
        return new OcrLayoutRegion(OcrLayoutContentKind.Paragraph, bounds, classifiedLines, paragraphText);
    }

    /// <summary>用"行首 left 聚类 + 列表标记 + 缩进续行"识别列表结构。
    ///
    /// 算法步骤：
    /// 1. 把所有 line 按 Bounds.Left 在 medianHeight * ListLeftAlignToleranceRatio 容差内聚类，
    ///    其中"行数最多的簇"即为列表项的对齐基线。
    /// 2. 若该 cluster 至少 2 行，且其中至少一行 LooksLikeListItem 命中（编号 / 项目符号 / 中文一二三 / 括号编号），
    ///    则把这些行标记为 ListItem。
    /// 3. 其余行根据相对 primary left 的偏移分类：
    ///    - 偏移在 [MinIndent, MaxIndent] × medianHeight 区间 → ListContinuation（缩进续行）
    ///    - 否则保持 Text
    /// 4. 当 ListItem + ListContinuation 占比 ≥ ListMinClassifiedLineRatio 时，整块判为 List 区域。
    ///
    /// 为什么不直接按"任何一行 LooksLikeListItem 即判列表"：
    /// 单行命中容易把"参考文献：[1] xxx"这种段落里偶发的中括号编号误判为列表。
    /// 加上左对齐 + 多数派两个几何信号能大幅压低误报。</summary>
    private static (bool IsList, IReadOnlyList<OcrLayoutLine> Lines) ClassifyListLines(
        IReadOnlyList<OcrLayoutLine> lines,
        float medianHeight,
        OcrLayoutOptions options)
    {
        if (lines.Count < 2) return (false, lines);

        var alignTolerance = Math.Max(2f, medianHeight * options.ListLeftAlignToleranceRatio);
        var clusters = ClusterLinesByLeft(lines, alignTolerance);
        if (clusters.Count == 0) return (false, lines);

        var primary = clusters
            .OrderByDescending(c => c.Members.Count)
            .ThenBy(c => c.Center)
            .First();
        if (primary.Members.Count < 2) return (false, lines);

        var hasListMarker = primary.Members.Any(l => LooksLikeListItem(l.Text));
        if (!hasListMarker) return (false, lines);

        var primaryLeft = primary.Center;
        var continuationMin = medianHeight * options.ListContinuationMinIndentRatio;
        var continuationMax = medianHeight * options.ListContinuationMaxIndentRatio;

        var classified = new List<OcrLayoutLine>(lines.Count);
        var classifiedCount = 0;
        foreach (var line in lines)
        {
            var delta = line.Bounds.Left - primaryLeft;
            var lineHasMarker = LooksLikeListItem(line.Text);
            OcrLayoutLineKind kind;
            if (Math.Abs(delta) <= alignTolerance)
            {
                // 左对齐到列表项基线的行：
                // - 行首带列表标记 -> 新列表项首行 (ListItem)
                // - 行首不带列表标记 -> 上一项的续行 (ListContinuation)。
                //   中文"段落式编号列表"非常常见——列表项内被视觉换行后，下一行无缩进直接顶到与编号同列；
                //   若一律按"左对齐 = ListItem"处理，会把"一、...不属\n于伊朗"这类截断行误判为独立项，
                //   导致 ComposeListText 永远换行不合并。
                kind = lineHasMarker
                    ? OcrLayoutLineKind.ListItem
                    : OcrLayoutLineKind.ListContinuation;
                classifiedCount++;
            }
            else if (delta >= continuationMin && delta <= continuationMax)
            {
                // 缩进位置的行可能是两种之一：嵌套子列表项（行首带 1./-/• 等标记）或上一项的续行（无标记）。
                // 区分关键是行首是否有列表标记，否则会把 "1. 父项\n   - 子项 A" 误合并成
                // "1. 父项- 子项 A" 这种破坏结构的输出。
                kind = lineHasMarker
                    ? OcrLayoutLineKind.ListItem
                    : OcrLayoutLineKind.ListContinuation;
                classifiedCount++;
            }
            else
            {
                kind = OcrLayoutLineKind.Text;
            }

            classified.Add(line with { Kind = kind });
        }

        var ratio = (float)classifiedCount / classified.Count;
        var isList = ratio >= options.ListMinClassifiedLineRatio;
        return (isList, classified);
    }

    /// <summary>按 Bounds.Left 把行聚成对齐簇。流式累加均值，保证 cluster center 不会被
    /// "极端 left"拖偏。tolerance 用 medianHeight 派生，比绝对像素阈值在多分辨率下更鲁棒。</summary>
    private static List<LineCluster> ClusterLinesByLeft(IReadOnlyList<OcrLayoutLine> lines, float tolerance)
    {
        var clusters = new List<LineCluster>();
        foreach (var line in lines.OrderBy(l => l.Bounds.Left))
        {
            LineCluster? match = null;
            foreach (var c in clusters)
            {
                if (Math.Abs(c.Center - line.Bounds.Left) <= tolerance) { match = c; break; }
            }

            if (match is null)
            {
                var created = new LineCluster { Center = line.Bounds.Left };
                created.Members.Add(line);
                clusters.Add(created);
            }
            else
            {
                match.Add(line);
            }
        }
        return clusters;
    }

    /// <summary>列表区域文本拼接：
    /// - ListItem / Text 行各占一行；
    /// - ListContinuation 行与上一行合并（沿用 WrappedLineJoiner 的中英文 / 标点连接规则）；
    /// - 不做"段尾合并"判定，列表项之间永远保留换行。
    ///
    /// 这里特意不补 "- " / "1. " 之类的 markdown 前缀：OCR 原文如果已带编号或项目符号，
    /// 强行二次包装会出现 "- 1. xxx" 这种重复；如果没带，简单地保留几何换行也比生造编号更安全。</summary>
    private static string ComposeListText(IReadOnlyList<OcrLayoutLine> lines, float medianHeight, OcrLayoutOptions options)
    {
        if (lines.Count == 0) return "";

        var sb = new StringBuilder();
        string? previousText = null;
        OcrLayoutLine? previousLine = null;
        var intraGapThreshold = medianHeight * options.MaxIntraRegionLineGapRatio;
        foreach (var line in lines)
        {
            // 合并需要同时满足以下三个条件：
            // 1. 当前行被分类为 ListContinuation
            // 2. 上一行末尾不是句末标点（。！？；：…）——句末意味着上一项收尾，紧跟行更可能是小说明 / 次级标题
            // 3. 与上一行的 vertical gap 不超过段内典型行距——避免几何上明显间隔的两行被错误粘连
            var verticalGap = previousLine is { } prev ? Math.Max(0f, line.Bounds.Top - prev.Bounds.Bottom) : 0f;
            var canMerge = line.Kind == OcrLayoutLineKind.ListContinuation
                && previousText is not null
                && sb.Length > 0
                && !EndsWithSentenceBoundary(previousText)
                && verticalGap <= intraGapThreshold;

            if (canMerge)
            {
                var joiner = WrappedLineJoiner(previousText!, line.Text);
                if (joiner.RemoveTrailingHyphen && sb.Length > 0 && sb[^1] == '-')
                {
                    sb.Length--;
                }
                sb.Append(joiner.Text);
                sb.Append(line.Text);
                previousText = previousText + joiner.Text + line.Text;
                previousLine = line;
                continue;
            }

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line.Text);
            previousText = line.Text;
            previousLine = line;
        }

        return sb.ToString().Trim();
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

    private static string ComposeParagraphText(IReadOnlyList<OcrLayoutLine> lines, OcrLayoutRect regionBounds, float medianHeight, OcrLayoutOptions options)
    {
        if (lines.Count == 0) return "";

        // 折行判定参考宽度用 P90 行宽，而不是 regionBounds.Width：
        // 区域内只要有一行特别长，整块的 regionBounds.Width 就会被拉宽，
        // 正常段尾短行的 fill ratio 会被低估，被错切成段落分隔。
        // P90 排除掉极端长行的影响，让 fill ratio 用"主流行宽"做基准。
        var referenceWidth = ComputeReferenceLineWidth(lines, options.ReferenceLineWidthPercentile, regionBounds.Width);
        var referenceBounds = new OcrLayoutRect(
            regionBounds.Left,
            regionBounds.Top,
            regionBounds.Left + referenceWidth,
            regionBounds.Bottom);

        var sb = new StringBuilder(lines[0].Text);
        var previousText = lines[0].Text;

        for (var i = 1; i < lines.Count; i++)
        {
            var nextText = lines[i].Text;
            if (ShouldMergeWrappedLine(lines[i - 1], lines[i], referenceBounds, medianHeight, options))
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

    /// <summary>取 lines 中行宽的指定百分位作为参考宽度。fallback 用于
    /// 行数过少 / 行宽全为 0 等异常场景，保证返回值始终 > 0。</summary>
    private static float ComputeReferenceLineWidth(IReadOnlyList<OcrLayoutLine> lines, float percentile, float fallback)
    {
        if (lines.Count == 0) return Math.Max(1f, fallback);
        var widths = lines
            .Select(l => l.Bounds.Width)
            .Where(w => w > 0)
            .OrderBy(w => w)
            .ToList();
        if (widths.Count == 0) return Math.Max(1f, fallback);
        if (widths.Count == 1) return widths[0];

        var clamped = Math.Clamp(percentile, 0f, 1f);
        var index = (int)Math.Ceiling(widths.Count * clamped) - 1;
        index = Math.Clamp(index, 0, widths.Count - 1);
        return widths[index];
    }

    private static bool ShouldMergeWrappedLine(OcrLayoutLine previous, OcrLayoutLine next, OcrLayoutRect regionBounds, float medianHeight, OcrLayoutOptions options)
    {
        var prevText = previous.Text.Trim();
        var nextText = next.Text.Trim();
        if (prevText.Length == 0 || nextText.Length == 0) return false;

        // 二次 gap 切分：BuildTextComponents 只在 gap > 1.65 倍字高时切大区域，
        // 0.6 ~ 1.6 倍字高的间隔会留在同一 region 内但视觉上明显是独立条目。
        // 这里用 medianHeight × MaxIntraRegionLineGapRatio (默认 0.6) 兜底，
        // 让"行首无 marker、行末无标点、行宽接近"的并列条目也能保持换行。
        var verticalGap = Math.Max(0f, next.Bounds.Top - previous.Bounds.Bottom);
        if (verticalGap > medianHeight * options.MaxIntraRegionLineGapRatio) return false;

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

        // ASCII 项目符号：要求标记后紧跟空白，避免 "-7"、"*x" 被误判
        if (t.Length >= 2 && (t[0] is '-' or '*') && char.IsWhiteSpace(t[1])) return true;

        // bullet '•' 几乎只作为项目符号出现，允许后续空白被 OCR 吞掉；
        // '·' 也可能是 "A·B" 这种间隔符，仍要求后跟空白
        if (t.Length >= 1 && t[0] == '•') return true;
        if (t.Length >= 2 && t[0] == '·' && char.IsWhiteSpace(t[1])) return true;

        // 阿拉伯数字编号：1. / 1、 / 1) / 1．/ 1）。
        // 原版强制要求编号后紧跟空白，OCR 在中文场景下常把 "1.内容" 当作一个 token 输出导致漏判；
        // 这里放宽为"编号 + 非数字字符"，仍显式排除 "1.5"、"3.14"、"2026.05" 这类小数 / 版本号字面量
        var digitEnd = 0;
        while (digitEnd < t.Length && char.IsDigit(t[digitEnd])) digitEnd++;
        if (digitEnd > 0 && digitEnd < t.Length)
        {
            var sep = t[digitEnd];
            if (sep is '.' or '．' or ')' or '）' or '、')
            {
                if (digitEnd + 1 == t.Length) return true;
                var next = t[digitEnd + 1];
                if (!char.IsDigit(next)) return true;
            }
        }

        // 中文汉字数字编号：一、 / 二． / 十、，常见于学术、说明文档类 OCR 文本
        if (t.Length >= 2 && IsChineseOrdinalDigit(t[0]))
        {
            var ordEnd = 1;
            while (ordEnd < t.Length && IsChineseOrdinalDigit(t[ordEnd])) ordEnd++;
            if (ordEnd < t.Length && t[ordEnd] is '、' or '.' or '．') return true;
        }

        // 括号包围的编号：(1) / （1） / (一) / （一）
        if (t.Length >= 3 && (t[0] is '(' or '（'))
        {
            var close = t[0] == '(' ? ')' : '）';
            var closeIdx = t.IndexOf(close, 1);
            if (closeIdx > 1)
            {
                var allDigitOrOrdinal = true;
                for (var k = 1; k < closeIdx; k++)
                {
                    if (!char.IsDigit(t[k]) && !IsChineseOrdinalDigit(t[k]))
                    {
                        allDigitOrOrdinal = false;
                        break;
                    }
                }
                if (allDigitOrOrdinal) return true;
            }
        }

        return false;
    }

    private static bool IsChineseOrdinalDigit(char ch)
        => ch is '一' or '二' or '三' or '四' or '五' or '六' or '七' or '八' or '九'
            or '十' or '百' or '千' or '〇' or '零';

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

    /// <summary>列表分类用的行首左缘聚类。Center 是簇内 Left 的累积均值，
    /// 流式更新避免一次性 ToArray 在大 component 下的额外分配。</summary>
    private sealed class LineCluster
    {
        public float Center { get; set; }
        public List<OcrLayoutLine> Members { get; } = new();

        public void Add(OcrLayoutLine line)
        {
            var n = Members.Count;
            Center = ((Center * n) + line.Bounds.Left) / (n + 1);
            Members.Add(line);
        }
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

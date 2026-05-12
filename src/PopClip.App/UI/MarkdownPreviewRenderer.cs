using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PopClip.App.UI;

internal static class MarkdownPreviewRenderer
{
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex UnorderedList = new(@"^\s*[-*+]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex OrderedList = new(@"^\s*\d+[.)]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex Quote = new(@"^\s*>\s?(.+)$", RegexOptions.Compiled);
    private static readonly Regex TableLike = new(@"^\s*\|.+\|\s*$", RegexOptions.Compiled);

    public static bool LooksLikeMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var score = 0;
        var lines = NormalizeLines(text).Split('\n');
        foreach (var line in lines)
        {
            if (Heading.IsMatch(line)) score += 2;
            else if (UnorderedList.IsMatch(line) || OrderedList.IsMatch(line)) score++;
            else if (Quote.IsMatch(line)) score++;
            else if (TableLike.IsMatch(line)) score++;
            else if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) score += 2;
            if (score >= 2) return true;
        }

        if (Regex.IsMatch(text, @"\[[^\]]+\]\([^)]+\)")) score++;
        if (Regex.IsMatch(text, @"(`[^`]+`|\*\*[^*]+\*\*|__[^_]+__)")) score++;
        return score >= 2;
    }

    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI"),
            FontSize = 13,
            PagePadding = new Thickness(0),
            LineHeight = 20,
        };

        var lines = NormalizeLines(markdown).Split('\n');
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        var inCode = false;
        Paragraph? code = null;

        void FlushParagraph()
        {
            if (paragraph.Inlines.Count == 0) return;
            doc.Blocks.Add(paragraph);
            paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (!inCode)
                {
                    inCode = true;
                    code = new Paragraph
                    {
                        Margin = new Thickness(0, 4, 0, 10),
                        Padding = new Thickness(10),
                        Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
                        FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                        FontSize = 12,
                    };
                    doc.Blocks.Add(code);
                }
                else
                {
                    inCode = false;
                    code = null;
                }
                continue;
            }

            if (inCode)
            {
                code?.Inlines.Add(new Run(line + Environment.NewLine));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            var heading = Heading.Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                var level = heading.Groups[1].Value.Length;
                var p = new Paragraph
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = level <= 1 ? 22 : level == 2 ? 18 : 15,
                    Margin = new Thickness(0, level <= 2 ? 12 : 8, 0, 6),
                };
                AddInlineMarkdown(p, heading.Groups[2].Value);
                doc.Blocks.Add(p);
                continue;
            }

            var bullet = UnorderedList.Match(line);
            var numbered = OrderedList.Match(line);
            if (bullet.Success || numbered.Success)
            {
                FlushParagraph();
                var p = new Paragraph { Margin = new Thickness(8, 1, 0, 3) };
                p.Inlines.Add(new Run(bullet.Success ? "• " : "1. ") { FontWeight = FontWeights.SemiBold });
                AddInlineMarkdown(p, (bullet.Success ? bullet : numbered).Groups[1].Value);
                doc.Blocks.Add(p);
                continue;
            }

            var quote = Quote.Match(line);
            if (quote.Success)
            {
                FlushParagraph();
                var p = new Paragraph
                {
                    Margin = new Thickness(8, 2, 0, 8),
                    Padding = new Thickness(10, 4, 0, 4),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD2, 0xDC)),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x65, 0x77)),
                };
                AddInlineMarkdown(p, quote.Groups[1].Value);
                doc.Blocks.Add(p);
                continue;
            }

            if (TableLike.IsMatch(line))
            {
                FlushParagraph();
                var p = new Paragraph
                {
                    Margin = new Thickness(0, 2, 0, 3),
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                    FontSize = 12,
                };
                p.Inlines.Add(new Run(line));
                doc.Blocks.Add(p);
                continue;
            }

            if (paragraph.Inlines.Count > 0) paragraph.Inlines.Add(new LineBreak());
            AddInlineMarkdown(paragraph, line);
        }

        FlushParagraph();
        return doc;
    }

    private static void AddInlineMarkdown(Paragraph paragraph, string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (TryConsume(text, i, "`", "`", out var code, out var next))
            {
                paragraph.Inlines.Add(new Run(code)
                {
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                    Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF6)),
                });
                i = next;
                continue;
            }
            if (TryConsume(text, i, "**", "**", out var bold, out next)
                || TryConsume(text, i, "__", "__", out bold, out next))
            {
                paragraph.Inlines.Add(new Bold(new Run(bold)));
                i = next;
                continue;
            }
            if (TryConsume(text, i, "*", "*", out var italic, out next)
                || TryConsume(text, i, "_", "_", out italic, out next))
            {
                paragraph.Inlines.Add(new Italic(new Run(italic)));
                i = next;
                continue;
            }

            var link = Regex.Match(text[i..], @"^\[([^\]]+)\]\(([^)]+)\)");
            if (link.Success)
            {
                var run = new Run(link.Groups[1].Value)
                {
                    Foreground = Brushes.DodgerBlue,
                    TextDecorations = TextDecorations.Underline,
                };
                paragraph.Inlines.Add(run);
                i += link.Length;
                continue;
            }

            var nextSpecial = FindNextSpecial(text, i + 1);
            paragraph.Inlines.Add(new Run(text[i..nextSpecial]));
            i = nextSpecial;
        }
    }

    private static bool TryConsume(string text, int start, string open, string close, out string value, out int next)
    {
        value = "";
        next = start;
        if (!text.AsSpan(start).StartsWith(open, StringComparison.Ordinal)) return false;
        var contentStart = start + open.Length;
        var closeAt = text.IndexOf(close, contentStart, StringComparison.Ordinal);
        if (closeAt < contentStart) return false;
        value = text[contentStart..closeAt];
        next = closeAt + close.Length;
        return value.Length > 0;
    }

    private static int FindNextSpecial(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] is '`' or '*' or '_' or '[') return i;
        }
        return text.Length;
    }

    private static string NormalizeLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

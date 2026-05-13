using System.Globalization;
using System.Windows.Data;
using MahApps.Metro.IconPacks;
using FontFamily = System.Windows.Media.FontFamily;

namespace PopClip.App.UI;

/// <summary>把 IconKey 字符串映射为可显示的字形字符串。
/// Upper/Lower/Title 等大小写动作直接以字面字符（A/a/T）作为视觉提示，避免引入图标字体不存在的字符</summary>
internal sealed class IconKeyToGlyphConverter : IValueConverter
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Copy"] = "\uE8C8",
        ["Search"] = "\uE721",
        ["Google"] = "\uE721",
        ["Bing"] = "\uE721",
        ["Paste"] = "\uE77F",
        ["Translate"] = "\uF2B7",
        ["Upper"] = "A",
        ["Lower"] = "a",
        ["Title"] = "T",
        ["Url"] = "\uE71B",
        ["Mail"] = "\uE715",
        ["Calc"] = "\uE8EF",
        ["Count"] = "\uE8FD",
        ["Script"] = "\uE756",
        ["ClipboardHistory"] = "\uE81C",
        ["History"] = "\uE81C",
        ["Ai"] = "\uF0E7",
        ["AiChat"] = "\uE8F2",
        ["AiSummary"] = "\uE8FD",
        ["AiRewrite"] = "\uE70F",
        ["AiTranslate"] = "\uF2B7",
        ["AiExplain"] = "\uE946",
        ["AiReply"] = "\uE8F2",
        ["AiTidy"] = "\uE8A4",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string key && Map.TryGetValue(key, out var glyph)) return glyph;
        return "\u2022";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();

    /// <summary>给定 IconKey 返回 PUA 字符串（图标字体）或普通字符串</summary>
    internal static string Resolve(string? key)
    {
        if (key is not null && Map.TryGetValue(key, out var glyph)) return glyph;
        return "\u2022";
    }
}

/// <summary>根据 IconKey 选择字体：PUA (U+E000~U+F8FF) 走图标字体，否则走系统默认 UI 字体。
/// 这样大小写类按钮的 A/a/T 才不会因为缺字呈现为方块</summary>
internal sealed class IconKeyToFontFamilyConverter : IValueConverter
{
    private static readonly FontFamily Symbol = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    // 中文优先：避免中英混排时按钮文字字号/笔画粗细出现错配
    private static readonly FontFamily Text = new("Microsoft YaHei UI, Microsoft YaHei, 微软雅黑, PingFang SC, Segoe UI");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var glyph = IconKeyToGlyphConverter.Resolve(value as string);
        if (glyph.Length > 0)
        {
            var ch = glyph[0];
            if (ch >= '\uE000' && ch <= '\uF8FF') return Symbol;
        }
        return Text;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

internal sealed class IconKeyToMaterialDesignKindConverter : IValueConverter
{
    private static readonly Dictionary<string, PackIconMaterialDesignKind> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Copy"] = PackIconMaterialDesignKind.ContentCopyRound,
        ["Search"] = PackIconMaterialDesignKind.SearchRound,
        ["Google"] = PackIconMaterialDesignKind.SearchRound,
        ["Bing"] = PackIconMaterialDesignKind.SearchRound,
        ["Paste"] = PackIconMaterialDesignKind.ContentPasteRound,
        ["Translate"] = PackIconMaterialDesignKind.TranslateRound,
        ["Upper"] = PackIconMaterialDesignKind.TextIncreaseRound,
        ["Lower"] = PackIconMaterialDesignKind.TextDecreaseRound,
        ["Title"] = PackIconMaterialDesignKind.TitleRound,
        ["Url"] = PackIconMaterialDesignKind.OpenInNewRound,
        ["Mail"] = PackIconMaterialDesignKind.EmailRound,
        ["Calc"] = PackIconMaterialDesignKind.CalculateRound,
        ["Count"] = PackIconMaterialDesignKind.FormatListNumberedRound,
        ["Script"] = PackIconMaterialDesignKind.CodeRound,
        ["ClipboardHistory"] = PackIconMaterialDesignKind.HistoryRound,
        ["History"] = PackIconMaterialDesignKind.HistoryRound,
        ["Ai"] = PackIconMaterialDesignKind.SmartToyRound,
        ["AiChat"] = PackIconMaterialDesignKind.ForumRound,
        ["AiSummary"] = PackIconMaterialDesignKind.SummarizeRound,
        ["AiRewrite"] = PackIconMaterialDesignKind.AutoFixHighRound,
        ["AiTranslate"] = PackIconMaterialDesignKind.TranslateRound,
        ["AiExplain"] = PackIconMaterialDesignKind.InfoRound,
        ["AiReply"] = PackIconMaterialDesignKind.ReplyRound,
        ["AiTidy"] = PackIconMaterialDesignKind.FormatLineSpacingRound,
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string key && Map.TryGetValue(key, out var kind)) return kind;
        return PackIconMaterialDesignKind.QuestionMarkRound;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

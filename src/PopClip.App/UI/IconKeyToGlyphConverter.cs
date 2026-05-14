using System.Globalization;
using System.Windows.Data;
using MahApps.Metro.IconPacks;
using FontFamily = System.Windows.Media.FontFamily;

namespace PopClip.App.UI;

/// <summary>把 IconKey 字符串映射为可显示的字形字符串。
/// Upper/Lower/Title 等大小写动作直接以字面字符（A/a/T）作为视觉提示，避免引入图标字体不存在的字符</summary>
internal sealed class IconKeyToGlyphConverter : IValueConverter
{
    // Segoe Fluent Icons 兜底字形：仅当主路径（PackIconMaterialDesign）不可用时才会显示
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Copy"] = "\uE8C8",
        ["Search"] = "\uE721",
        ["Paste"] = "\uE77F",
        ["Translate"] = "\uF2B7",
        ["Upper"] = "A",
        ["Lower"] = "a",
        ["Title"] = "T",
        ["Url"] = "\uE71B",
        ["Mail"] = "\uE715",
        ["Calc"] = "\uE8EF",
        ["Count"] = "\uE8FD",
        ["ClipboardHistory"] = "\uE81C",
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
    // ============ 内置功能图标（runtime 内部引用，不参与用户图标选择器） ============
    // 改这里时务必同步检查 IconChoiceCatalog.UserSelectable 是否需要剔除/新增
    private static readonly Dictionary<string, PackIconMaterialDesignKind> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Copy"] = PackIconMaterialDesignKind.ContentCopyRound,
        ["Search"] = PackIconMaterialDesignKind.SearchRound,
        // ContentPasteGoRound 在剪贴板符号上多一个朝右箭头，
        // 与 ContentCopyRound（两张方框叠层）视觉上一眼可辨；纯 ContentPasteRound 与复制图标几乎相同
        ["Paste"] = PackIconMaterialDesignKind.ContentPasteGoRound,
        ["Translate"] = PackIconMaterialDesignKind.TranslateRound,
        ["Upper"] = PackIconMaterialDesignKind.TextIncreaseRound,
        ["Lower"] = PackIconMaterialDesignKind.TextDecreaseRound,
        ["Title"] = PackIconMaterialDesignKind.TitleRound,
        ["Url"] = PackIconMaterialDesignKind.OpenInNewRound,
        ["Mail"] = PackIconMaterialDesignKind.EmailRound,
        ["Calc"] = PackIconMaterialDesignKind.CalculateRound,
        ["Count"] = PackIconMaterialDesignKind.FormatListNumberedRound,
        ["ClipboardHistory"] = PackIconMaterialDesignKind.HistoryRound,
        ["Ai"] = PackIconMaterialDesignKind.SmartToyRound,
        ["AiChat"] = PackIconMaterialDesignKind.ForumRound,
        // ============ 内置 prompt 模板专用图标（不进用户图标选择器） ============
        // 这些 key 仅供 PromptTemplateLibrary.Builtin 引用，写入 actions.json 后通过 IconLocked=true 锁死不可改
        ["AiSummary"] = PackIconMaterialDesignKind.SummarizeRound,
        ["AiRewrite"] = PackIconMaterialDesignKind.AutoFixHighRound,
        ["AiTranslate"] = PackIconMaterialDesignKind.TranslateRound,
        ["AiExplain"] = PackIconMaterialDesignKind.InfoRound,
        ["AiReply"] = PackIconMaterialDesignKind.ReplyRound,
        ["AiTidy"] = PackIconMaterialDesignKind.FormatLineSpacingRound,
        // ============ 通用图形/语义图标（用户在自定义动作里可选） ============
        // 这里的 key 不能与上方"内置功能"重复，否则同一图标会跨语义共用
        ["Star"] = PackIconMaterialDesignKind.StarRound,
        ["Heart"] = PackIconMaterialDesignKind.FavoriteRound,
        ["Bookmark"] = PackIconMaterialDesignKind.BookmarkRound,
        ["Flag"] = PackIconMaterialDesignKind.FlagRound,
        ["Pin"] = PackIconMaterialDesignKind.PushPinRound,
        ["Key"] = PackIconMaterialDesignKind.KeyRound,
        ["Lock"] = PackIconMaterialDesignKind.LockRound,
        ["Bulb"] = PackIconMaterialDesignKind.LightbulbRound,
        ["Bolt"] = PackIconMaterialDesignKind.BoltRound,
        ["Sparkle"] = PackIconMaterialDesignKind.AutoAwesomeRound,
        ["Fire"] = PackIconMaterialDesignKind.LocalFireDepartmentRound,
        ["Diamond"] = PackIconMaterialDesignKind.DiamondRound,
        ["Label"] = PackIconMaterialDesignKind.LabelRound,
        ["Layers"] = PackIconMaterialDesignKind.LayersRound,
        ["Build"] = PackIconMaterialDesignKind.BuildRound,
        ["Extension"] = PackIconMaterialDesignKind.ExtensionRound,
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string key && Map.TryGetValue(key, out var kind)) return kind;
        return PackIconMaterialDesignKind.QuestionMarkRound;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>"动作图标下拉选择器"里的可选项。
/// IconKey 必须在 IconKeyToMaterialDesignKindConverter.Map 中有对应图形</summary>
public sealed record IconChoice(string IconKey, string Label);

/// <summary>用户给自定义动作选图标时可见的清单。
/// 严格排除已经被内置功能（复制/搜索/翻译/...）和内置 AI（AiChat 等）占用的 key，
/// 保证图标承载的语义对用户唯一可识别</summary>
public static class IconChoiceCatalog
{
    public static IReadOnlyList<IconChoice> UserSelectable { get; } = new IconChoice[]
    {
        new("Star", "星标"),
        new("Heart", "心形"),
        new("Bookmark", "书签"),
        new("Flag", "旗帜"),
        new("Pin", "图钉"),
        new("Key", "钥匙"),
        new("Lock", "锁"),
        new("Bulb", "灯泡"),
        new("Bolt", "闪电"),
        new("Sparkle", "闪光"),
        new("Fire", "火焰"),
        new("Diamond", "钻石"),
        new("Label", "标签"),
        new("Layers", "图层"),
        new("Build", "扳手"),
        new("Extension", "拼图"),
    };
}

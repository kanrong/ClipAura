using System.Globalization;
using System.Windows.Data;

namespace PopClip.App.UI;

/// <summary>把 IconKey 字符串映射为 Segoe UI Symbol 字形，避免引入图标资源依赖</summary>
internal sealed class IconKeyToGlyphConverter : IValueConverter
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Copy"] = "\uE8C8",
        ["Google"] = "\uE721",
        ["Bing"] = "\uE721",
        ["Translate"] = "\uF2B7",
        ["Upper"] = "A",
        ["Lower"] = "a",
        ["Title"] = "T",
        ["Url"] = "\uE71B",
        ["Mail"] = "\uE715",
        ["Calc"] = "\uE8EF",
        ["Count"] = "\uE8FD",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string key && Map.TryGetValue(key, out var glyph)) return glyph;
        return "\u2022";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

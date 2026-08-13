using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PopClip.Actions.BuiltIn;
using PopClip.App.Config;
using PopClip.App.Hosting;
using PopClip.App.Ocr;
using PopClip.App.Services;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Session;
using PopClip.Hooks;
using System.Windows.Automation;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace PopClip.App.UI;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow, INotifyPropertyChanged
{

    private void OnToolbarOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateToolbarOpacityLabel(e.NewValue);
        CommitAll();
    }

    /// <summary>颜色主题选择变化：当前选择写入 settings 并立即应用到全局画刷。
    /// CommitAll → AppHost.ApplyRuntimeSettings → ThemeManager.Apply，
    /// 后者会同步更新 Application.Resources，让所有窗口随即换肤</summary>
    private void OnToolbarThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        CommitAll();
    }

    private void UpdateToolbarOpacityLabel(double value)
    {
        if (ToolbarOpacityValue is null) return;
        ToolbarOpacityValue.Text = (value * 100).ToString("0") + "%";
    }

    /// <summary>把系统已安装字体填入"界面字体 / 浮窗字体"两个 ComboBox。
    /// 使用 LocalizedFontNames 优先取中文族名，列表前补一个"默认"代表清空，
    /// 留空配置项是个常见姿势，IsEditable 允许用户键入未列出的字体名</summary>
    private void BindFontFamilyChoices()
    {
        var families = EnumerateSystemFontFamilies();
        // "默认字体"项实际写入的字符串是 ""，CommitAll 时会把它落到 settings；
        // FontFamilyHelper.ResolveUi/ResolveToolbar 看到空字符串后回退到内部链
        // （首选 Windows 当前消息字体 + 中文回退），所以切回此项就等同于"跟随 Windows 主体字体"。
        // 实际生效的字体名在下方"预览"文本中以原文显示，不再叠加"默认"前缀
        var defaultLabel = "默认字体";
        var choices = new List<FontFamilyChoice>(families.Count + 1)
        {
            new("", defaultLabel),
        };
        foreach (var name in families)
        {
            choices.Add(new FontFamilyChoice(name, name));
        }

        UiFontFamilyBox.ItemsSource = choices;
        UiFontFamilyBox.DisplayMemberPath = nameof(FontFamilyChoice.Label);
        UiFontFamilyBox.SelectedValuePath = nameof(FontFamilyChoice.Value);

        ToolbarFontFamilyBox.ItemsSource = choices;
        ToolbarFontFamilyBox.DisplayMemberPath = nameof(FontFamilyChoice.Label);
        ToolbarFontFamilyBox.SelectedValuePath = nameof(FontFamilyChoice.Value);

        SetFontComboValue(UiFontFamilyBox, _settings.UiFontFamily);
        SetFontComboValue(ToolbarFontFamilyBox, _settings.ToolbarFontFamily);
        RefreshFontPreviews();
    }

    private static IReadOnlyList<string> EnumerateSystemFontFamilies()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        try
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            var fallback = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies)
            {
                var name = LocalizedName(f, culture) ?? LocalizedName(f, fallback) ?? f.Source;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!seen.Add(name)) continue;
                list.Add(name);
            }
            list.Sort(StringComparer.CurrentCulture);
        }
        catch
        {
            // 极端环境下字体列表枚举失败，至少给一个空集合让用户可以手动输入
        }
        return list;
    }

    private static string? LocalizedName(System.Windows.Media.FontFamily family, System.Globalization.CultureInfo culture)
    {
        try
        {
            if (family.FamilyNames.TryGetValue(System.Windows.Markup.XmlLanguage.GetLanguage(culture.IetfLanguageTag), out var name))
            {
                return name;
            }
        }
        catch
        {
            // FamilyNames 取值在某些字体上抛 NotSupportedException；忽略后落回 Source
        }
        return null;
    }

    private static void SetFontComboValue(WpfComboBox combo, string fontFamily)
    {
        var trimmed = (fontFamily ?? "").Trim();
        if (combo.ItemsSource is IEnumerable<FontFamilyChoice> choices)
        {
            var hit = choices.FirstOrDefault(c => string.Equals(c.Value, trimmed, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                combo.SelectedValue = hit.Value;
                combo.Text = hit.Label;
                return;
            }
        }
        // 配置中保存了系统并未列出的字体名（例如用户手动改了 json）：保留原文本，
        // 让 ComboBox 通过 IsEditable=true 仍能显示并使用
        combo.SelectedValue = null;
        combo.Text = trimmed;
    }

    private void OnUiFontFamilyChanged(object sender, RoutedEventArgs e)
    {
        RefreshFontPreviews();
        CommitAll();
    }

    private void OnToolbarFontFamilyChanged(object sender, RoutedEventArgs e)
    {
        RefreshFontPreviews();
        CommitAll();
    }

    private void OnResetFontFamilies(object sender, RoutedEventArgs e)
    {
        SetFontComboValue(UiFontFamilyBox, "");
        SetFontComboValue(ToolbarFontFamilyBox, "");
        RefreshFontPreviews();
        CommitAll();
    }

    private void RefreshFontPreviews()
    {
        var uiInput = NormalizeFontFamilyInput(UiFontFamilyBox);
        var toolbarInput = NormalizeFontFamilyInput(ToolbarFontFamilyBox);
        var uiFont = FontFamilyHelper.ResolveUi(uiInput);
        var toolbarFont = FontFamilyHelper.ResolveToolbar(toolbarInput, uiInput);

        // 预览文本格式："字体名 · 示例"。
        // 留空时直接显示实际生效的字体名（Windows 当前消息字体），让用户对照下拉里的"默认字体"
        // 也能一眼看出真实效果
        var uiName = string.IsNullOrWhiteSpace(uiInput) ? FontFamilyHelper.PreferredUiName : uiInput;
        var toolbarName = string.IsNullOrWhiteSpace(toolbarInput)
            ? (string.IsNullOrWhiteSpace(uiInput) ? FontFamilyHelper.PreferredUiName : uiInput)
            : toolbarInput;

        try
        {
            UiFontPreview.FontFamily = new System.Windows.Media.FontFamily(uiFont);
            UiFontPreview.Text = $"{uiName} · 预览 AaBb 你好";
        }
        catch { /* 输入了无效族名也不要抛 */ }
        try
        {
            ToolbarFontPreview.FontFamily = new System.Windows.Media.FontFamily(toolbarFont);
            ToolbarFontPreview.Text = $"{toolbarName} · 预览 AaBb 你好";
        }
        catch { /* 同上 */ }
    }

    /// <summary>从 IsEditable ComboBox 中取用户输入或选项值。
    /// 1) 优先看 SelectedItem 是否就是已知的 FontFamilyChoice：是则使用其 Value（"默认字体"项 Value="" 直接命中）
    /// 2) 否则取 Text：用户键入了未列出的字体名时走这条分支
    /// 3) Text 以"默认 / 系统默认"等中文 Label 开头时归一为空，避免被误当作字体名</summary>
    private static string NormalizeFontFamilyInput(WpfComboBox combo)
    {
        if (combo.SelectedItem is FontFamilyChoice picked)
        {
            return picked.Value?.Trim() ?? "";
        }
        var text = combo.Text?.Trim() ?? "";
        if (text.StartsWith("默认字体", StringComparison.Ordinal)) return "";
        if (text.StartsWith("系统默认", StringComparison.Ordinal)) return "";
        if (text.StartsWith("默认", StringComparison.Ordinal)) return "";
        return text;
    }

    private void OnPopupModeChanged(object sender, SelectionChangedEventArgs e)
    {
        var mode = SelectedTag(PopupModeBox);
        PopupDelayBox.IsEnabled = mode == nameof(SelectionPopupMode.Delayed);
        HoverDelayBox.IsEnabled = mode == nameof(SelectionPopupMode.HoverStill);
        RequiredModifierBox.IsEnabled = mode == nameof(SelectionPopupMode.ModifierRequired);
    }

    /// <summary>从外观页控件当前值派生预览的 Margin / Padding / 字号 / 阴影 / 透明度 等参数。
    /// 主题与字体走 DynamicResource 自动跟随，不需要在这里推送</summary>
    private void RefreshToolbarPreview()
    {
        if (DisplayIconAndText is null) return;
        var iconOn = DisplayIconOnly.IsChecked != true && DisplayTextOnly.IsChecked != true
            || DisplayIconOnly.IsChecked == true;
        var textOn = DisplayIconOnly.IsChecked != true && DisplayTextOnly.IsChecked != true
            || DisplayTextOnly.IsChecked == true;
        // 上面两条只决定"是否两者都开"，下面再单独处理 IconOnly / TextOnly 排他
        if (DisplayIconOnly.IsChecked == true) { iconOn = true; textOn = false; }
        else if (DisplayTextOnly.IsChecked == true) { iconOn = false; textOn = true; }
        PreviewIconVisibility = iconOn ? Visibility.Visible : Visibility.Collapsed;
        PreviewTextVisibility = textOn ? Visibility.Visible : Visibility.Collapsed;

        var radius = NumberBoxDouble(CornerRadiusBox, _settings.ToolbarCornerRadius, 0, 18);
        var spacing = NumberBoxDouble(ButtonSpacingBox, _settings.ToolbarButtonSpacing, 0, 10);
        var fontSize = NumberBoxDouble(ToolbarFontSizeBox, _settings.ToolbarFontSize, 10, 18);
        // 与浮窗实例化时一致：外圆角 = 用户值 + 1，给描边留 1px 抗锯齿余量
        PreviewCornerRadius = new CornerRadius(ResolvePreviewOuterCornerRadius(radius));
        PreviewButtonCornerRadius = new CornerRadius(0);
        PreviewButtonMargin = new Thickness(spacing, 0, spacing, 0);
        // 按钮内边距跟随密度档：紧凑 / 标准（默认）/ 宽松，让预览与真实浮窗呈相同观感
        var (padX, padY) = ResolvePreviewPadding();
        PreviewButtonPadding = new Thickness(padX, padY, padX, padY);
        PreviewFontSize = fontSize;
        PreviewIconFontSize = fontSize + 2;
        PreviewOpacity = Math.Clamp(ToolbarOpacitySlider.Value, 0.3, 1.0);

        var surface = SurfaceShadow.IsChecked == true
            ? ToolbarSurfaceStyle.Shadow
            : SurfaceBorder.IsChecked == true
                ? ToolbarSurfaceStyle.Border
                : ToolbarSurfaceStyle.ShadowAndBorder;
        // 阴影参数与 FloatingToolbar.ApplySurfaceStyle 严格对齐，
        // 切换"按钮风格"时预览与浮窗呈现完全相同的常规层次
        switch (surface)
        {
            case ToolbarSurfaceStyle.Shadow:
                PreviewBorderThickness = new Thickness(0);
                PreviewShadowBlurRadius = 9;
                PreviewShadowDepth = 2;
                PreviewShadowOpacity = 0.24;
                break;
            case ToolbarSurfaceStyle.Border:
                PreviewBorderThickness = new Thickness(1);
                PreviewShadowBlurRadius = 0;
                PreviewShadowDepth = 0;
                PreviewShadowOpacity = 0;
                break;
            default:
                PreviewBorderThickness = new Thickness(1);
                PreviewShadowBlurRadius = 7;
                PreviewShadowDepth = 1;
                PreviewShadowOpacity = 0.20;
                break;
        }
    }

    /// <summary>把当前选中的密度档位映射成按钮 padding。
    /// 与 FloatingToolbar.PaddingForDensity 数值严格一致，保持预览与真实浮窗同款观感</summary>
    private (double X, double Y) ResolvePreviewPadding()
    {
        if (DensityCompact is not null && DensityCompact.IsChecked == true) return (8, 5);
        if (DensityComfortable is not null && DensityComfortable.IsChecked == true) return (16, 13);
        return (12, 9);
    }

    private static double ResolvePreviewOuterCornerRadius(double userRadius)
        => Math.Clamp(userRadius + 1, 0, 19);
}

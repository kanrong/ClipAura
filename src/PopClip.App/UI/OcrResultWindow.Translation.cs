using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PopClip.App.Config;
using PopClip.App.Ocr;
using PopClip.App.Services;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;
using PopClip.Hooks.Window;
using WpfPoint = System.Windows.Point;
using WinRectangle = System.Drawing.Rectangle;

namespace PopClip.App.UI;

internal partial class OcrResultWindow : Window
{

    /// <summary>工具栏按钮触发：显示 / 隐藏结果图上的 OCR 原文贴图，并写回用户偏好。</summary>
    public void CommandToggleInlineText(Action<string>? toastSink = null)
    {
        _showingInlineText = !_showingInlineText;
        if (_showingInlineText)
        {
            RenderRecognizedTextOverlays();
            toastSink?.Invoke("已显示识别原文贴图");
        }
        else
        {
            HideRecognizedTextOverlays();
            toastSink?.Invoke("已隐藏识别原文贴图");
        }

        _settings.OcrInlineTextVisible = _showingInlineText;
        _saveSettings?.Invoke();
        UpdateStatusBar();
    }

    /// <summary>工具栏"翻译 / 原文"切换按钮入口。
    /// 状态机：原文 → 译文（按需调 AI） → 原文（隐藏 overlay） → 译文（直接走缓存）……
    ///
    /// 决定翻译范围：有选中段则译选中段，否则译全部段。这与"复制选中 / 复制全部"自适应行为一致，
    /// 只用一个按钮即可承载两种意图，UI 更紧凑。
    /// 缓存复用：_translatedText 在窗口生命周期内保留，多次切回译文显示时不需要重新请求 AI；
    /// 仅当用户主动调 CommandTranslateClear 清空，或新选中段未曾翻译时，才会发请求</summary>
    public void CommandToggleTranslation(Action<string>? toastSink = null)
    {
        if (_aiText is null || !_aiText.CanRun)
        {
            toastSink?.Invoke("请先在设置启用 AI 并配置 API Key");
            return;
        }
        if (_translating) { toastSink?.Invoke("翻译进行中，请稍候"); return; }

        if (_showingTranslation)
        {
            // 当前是译文模式 → 切回原文。仅清理 overlay 视觉层，保留 _translatedText 缓存
            HideAllTranslationOverlays();
            _showingTranslation = false;
            UpdateStatusBar();
            toastSink?.Invoke("已切换为原文显示");
            return;
        }

        // 当前是原文模式 → 切到译文。先决定本次要展示译文的 block 集合
        var indices = new List<int>();
        for (int i = 0; i < _selected.Length; i++) if (_selected[i]) indices.Add(i);
        if (indices.Count == 0)
        {
            // 无选中 → 全部段都译
            indices = Enumerable.Range(0, _result.Blocks.Count).ToList();
        }
        if (indices.Count == 0)
        {
            toastSink?.Invoke("没有识别到内容");
            return;
        }

        _ = ShowTranslationAsync(indices, toastSink);
    }

    // ============== 翻译 ==============

    /// <summary>翻译并渲染指定 block 集合的译文 overlay。
    /// - 命中缓存的段直接渲染（_translatedText[i] 不为空时跳过 AI 请求）；
    /// - 未缓存的段批量调用 AI 翻译，结果写入缓存。
    /// 全部成功后切到译文显示模式</summary>
    private async Task ShowTranslationAsync(IReadOnlyList<int> indices, Action<string>? toastSink = null)
    {
        var needTranslate = indices.Where(i => string.IsNullOrEmpty(_translatedText[i])).ToList();

        _translating = true;
        _toolbarWindow?.SetTranslateToggleEnabled(false);
        if (needTranslate.Count > 0)
        {
            _toolbarWindow?.SetStatus($"翻译中… ({needTranslate.Count} 段)");
        }

        try
        {
            if (needTranslate.Count > 0)
            {
                var sources = needTranslate.Select(i => _result.Blocks[i].Text).ToList();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                IReadOnlyList<string> translations;
                try
                {
                    translations = await _aiText!.TranslateBatchAsync(sources, cts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    toastSink?.Invoke("翻译超时（60 秒）");
                    return;
                }
                catch (Exception ex)
                {
                    _log.Warn("ocr translate failed", ("err", ex.Message), ("blocks", needTranslate.Count));
                    toastSink?.Invoke("翻译失败：" + ex.Message);
                    return;
                }

                for (int k = 0; k < needTranslate.Count && k < translations.Count; k++)
                {
                    var bi = needTranslate[k];
                    var translated = (translations[k] ?? "").Trim();
                    if (translated.Length == 0) continue;
                    _translatedText[bi] = translated;
                }
            }

            // 渲染所有 indices 的 overlay（缓存命中即直接画，没命中刚才已请求并写入缓存）
            int rendered = 0;
            foreach (var i in indices)
            {
                var text = _translatedText[i];
                if (string.IsNullOrEmpty(text)) continue;
                RenderTranslationOverlay(i, text);
                rendered++;
            }

            _showingTranslation = rendered > 0;
            int cached = indices.Count - needTranslate.Count;
            if (needTranslate.Count > 0 && cached > 0)
            {
                toastSink?.Invoke($"已翻译 {needTranslate.Count} 段（{cached} 段走缓存）");
            }
            else if (needTranslate.Count > 0)
            {
                toastSink?.Invoke($"已翻译 {needTranslate.Count} 段");
            }
            else
            {
                toastSink?.Invoke($"已显示译文 {indices.Count} 段");
            }
        }
        finally
        {
            _translating = false;
            UpdateStatusBar();
        }
    }

    /// <summary>把 TranslationCanvas 上所有 overlay 移除，但保留 _translatedText 缓存。
    /// 切换"译文 → 原文"显示时使用，让二次切回译文时直接复用缓存</summary>
    private void HideAllTranslationOverlays()
    {
        for (int i = 0; i < _translationOverlays.Length; i++)
        {
            if (_translationOverlays[i] is { } b)
            {
                TranslationCanvas.Children.Remove(b);
                _translationOverlays[i] = null;
            }
        }
    }

    private void RenderRecognizedTextOverlays()
    {
        HideRecognizedTextOverlays();
        for (var i = 0; i < _result.Blocks.Count; i++)
        {
            RenderRecognizedTextOverlay(i);
        }
    }

    private void HideRecognizedTextOverlays()
    {
        for (var i = 0; i < _recognizedTextOverlays.Length; i++)
        {
            if (_recognizedTextOverlays[i] is { } b)
            {
                RecognizedTextCanvas.Children.Remove(b);
                _recognizedTextOverlays[i] = null;
            }
        }
    }

    /// <summary>把 OCR 原文贴到对应 block 的 AABB 区域，方便用户直接核对识别文本与图像位置。</summary>
    private void RenderRecognizedTextOverlay(int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= _result.Blocks.Count) return;
        if (_result.SourceWidth <= 0 || _result.SourceHeight <= 0) return;

        var text = _result.Blocks[blockIndex].Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        var (innerW, innerH) = GetInnerSize();
        double sx = innerW / _result.SourceWidth;
        double sy = innerH / _result.SourceHeight;
        var (l, t, r, b) = _result.Blocks[blockIndex].Box.AABB();
        double left = l * sx;
        double top = t * sy;
        double width = Math.Max(20, (r - l) * sx);
        double height = Math.Max(12, (b - t) * sy);
        double fontSize = Math.Max(8, Math.Min(34, height * 0.60));

        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontFamily = (FontFamily?)TryFindResource("ToolbarFontFamily") ?? new FontFamily("Segoe UI"),
            Foreground = (Brush?)TryFindResource("ToolbarForeground") ?? Brushes.White,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 4, 0),
        };

        var border = new Border
        {
            Width = width,
            Height = height,
            Background = (Brush?)TryFindResource("ToolbarToastBackground") ?? Brushes.Black,
            BorderBrush = (Brush?)TryFindResource("ToolbarAccentSoft") ?? Brushes.SteelBlue,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Opacity = 0.94,
            Child = tb,
            ToolTip = text,
        };
        Canvas.SetLeft(border, left);
        Canvas.SetTop(border, top);
        RecognizedTextCanvas.Children.Add(border);
        _recognizedTextOverlays[blockIndex] = border;
    }

    /// <summary>给指定 block 的 polygon 位置贴一个译文 Border：
    /// 用 polygon 的 AABB 作为定位基准（足够覆盖原文），背景用主题色不透明遮住原文，
    /// 文本字号按 AABB 高度 * 0.62 自适应让单行刚好填满。
    /// 已有的 overlay 会被替换（同一 block 二次翻译时直接覆盖）。</summary>
    private void RenderTranslationOverlay(int blockIndex, string translated)
    {
        if (blockIndex < 0 || blockIndex >= _result.Blocks.Count) return;
        if (_result.SourceWidth <= 0 || _result.SourceHeight <= 0) return;

        if (_translationOverlays[blockIndex] is { } old)
        {
            TranslationCanvas.Children.Remove(old);
            _translationOverlays[blockIndex] = null;
        }

        var (innerW, innerH) = GetInnerSize();
        double sx = innerW / _result.SourceWidth;
        double sy = innerH / _result.SourceHeight;
        var (l, t, r, b) = _result.Blocks[blockIndex].Box.AABB();
        double left = l * sx;
        double top = t * sy;
        double width = Math.Max(20, (r - l) * sx);
        double height = Math.Max(12, (b - t) * sy);
        double fontSize = Math.Max(8, Math.Min(36, height * 0.62));

        var src = _result.Blocks[blockIndex].Text.Trim();
        bool unchanged = string.Equals(src, translated, StringComparison.Ordinal);

        var tb = new TextBlock
        {
            Text = translated,
            FontSize = fontSize,
            FontFamily = (FontFamily?)TryFindResource("ToolbarFontFamily") ?? new FontFamily("Segoe UI"),
            Foreground = (Brush?)TryFindResource("ToolbarForeground") ?? Brushes.White,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 4, 0),
            Opacity = unchanged ? 0.6 : 1.0,
        };

        var border = new Border
        {
            Width = width,
            Height = height,
            Background = (Brush?)TryFindResource("ToolbarBackground") ?? Brushes.Black,
            BorderBrush = (Brush?)TryFindResource("ToolbarAccentSoft") ?? Brushes.SteelBlue,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Child = tb,
            ToolTip = unchanged ? $"原文与译文一致：{translated}" : $"原文：{src}\n译文：{translated}",
        };
        Canvas.SetLeft(border, left);
        Canvas.SetTop(border, top);
        TranslationCanvas.Children.Add(border);
        _translationOverlays[blockIndex] = border;
    }

    private void ClearAllTranslations()
    {
        for (int i = 0; i < _translationOverlays.Length; i++)
        {
            if (_translationOverlays[i] is { } b)
            {
                TranslationCanvas.Children.Remove(b);
                _translationOverlays[i] = null;
            }
            // 同时清掉文本缓存：下次切到译文模式会重新调 AI（"清除"= 完全重置）
            _translatedText[i] = null;
        }
    }
}

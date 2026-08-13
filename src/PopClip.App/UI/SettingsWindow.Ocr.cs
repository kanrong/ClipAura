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

    /// <summary>OCR provider 选择 + 状态展示。
    ///
    /// 数据流：
    /// (1) 把 Registry 已注册的所有 provider 翻成 ComboBox 选项；
    /// (2) 第一项硬编码"自动"，对应空 id（settings.OcrProviderId = ""）；
    /// (3) 后续每项前缀 ✓/✗，让用户在不展开下拉的情况下也能看出可用性；
    /// (4) 详情区列出所有 provider 的当前状态，缺文件 / 初始化失败的会直接给出修复指引。
    ///
    /// 注意：provider.IsAvailable / UnavailableReason 是动态求值的（轻量文件 IO），
    /// 每次打开设置页都会重新看一遍当前状态，不需要 push 通知。</summary>
    private void BindOcrProviders()
    {
        // 自动模式 + 各 provider；id 用空串表示"自动"（settings 也存空串）
        var choices = new List<OcrProviderChoice>
        {
            new("", "自动 (按优先级选可用项)"),
        };
        foreach (var p in _ocrProviders.OrderByDescending(p => p.Priority))
        {
            var mark = p.IsAvailable ? "✓" : "✗";
            choices.Add(new OcrProviderChoice(p.Id, $"{mark}  {p.DisplayName}"));
        }
        OcrProviderBox.ItemsSource = choices;
        OcrProviderBox.SelectedValue = _settings.OcrProviderId ?? "";
        if (OcrProviderBox.SelectedItem is null) OcrProviderBox.SelectedIndex = 0;

        RefreshOcrProviderHint();
    }

    /// <summary>更新右侧"活跃 provider 简介"与底部"详情/缺件指引"。
    /// 选择切换 / 文件复制后调用即可，纯展示无副作用</summary>
    private void RefreshOcrProviderHint()
    {
        var pickedId = (OcrProviderBox.SelectedValue as string) ?? "";
        IOcrProvider? active;
        if (string.IsNullOrWhiteSpace(pickedId))
        {
            active = _ocrProviders.Where(p => p.IsAvailable).OrderByDescending(p => p.Priority).FirstOrDefault();
            OcrActiveProviderHint.Text = active is not null
                ? $"当前活跃: {active.DisplayName}"
                : "当前没有可用 OCR 后端";
        }
        else
        {
            active = _ocrProviders.FirstOrDefault(p => p.Id == pickedId);
            if (active is null)
                OcrActiveProviderHint.Text = $"未注册的 provider id: {pickedId}";
            else if (active.IsAvailable)
                OcrActiveProviderHint.Text = $"当前活跃: {active.DisplayName}";
            else
                OcrActiveProviderHint.Text = $"该 provider 暂不可用，将自动回退到其它可用 provider";
        }

        // 详情区把每个 provider 的状态都列出来，便于用户对照排查
        var sb = new System.Text.StringBuilder();
        foreach (var p in _ocrProviders.OrderByDescending(p => p.Priority))
        {
            sb.Append('•').Append(' ').Append(p.DisplayName);
            if (p.IsAvailable)
            {
                sb.AppendLine("  (可用)");
            }
            else
            {
                sb.Append("  (不可用)");
                sb.AppendLine();
                sb.Append("    ").AppendLine(p.UnavailableReason ?? "未知原因");
            }
        }
        OcrProviderDetailText.Text = sb.ToString().TrimEnd();
    }

    private void OnOcrProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendCommit) return;
        var picked = (OcrProviderBox.SelectedValue as string) ?? "";
        if (_settings.OcrProviderId == picked) return;
        _settings.OcrProviderId = picked;
        _store.SaveSettings(_settings);
        RefreshOcrProviderHint();
    }

    /// <summary>OCR provider ComboBox 的项目类型：Label 显示，Id 写入 settings。</summary>
    public sealed record OcrProviderChoice(string Id, string Label);

    /// <summary>把当前 settings.OcrResultMode / OcrResultWindowBordered 同步到两个 RadioButton + ToggleSwitch。
    /// 在 _suspendCommit=true 下勾选，避免 Checked 事件回写自己刚读到的值（无副作用，但减少日志噪音）</summary>
    private void BindOcrResultMode()
    {
        var prev = _suspendCommit;
        _suspendCommit = true;
        try
        {
            OcrModeInteractiveRadio.IsChecked = _settings.OcrResultMode == OcrResultMode.Interactive;
            OcrModeQuickRadio.IsChecked = _settings.OcrResultMode == OcrResultMode.Quick;
            OcrResultBorderedToggle.IsChecked = _settings.OcrResultWindowBordered;
            ScreenshotModeToolbarRadio.IsChecked = _settings.ScreenshotAfterCaptureMode == ScreenshotAfterCaptureMode.Toolbar;
            ScreenshotModeClipboardRadio.IsChecked = _settings.ScreenshotAfterCaptureMode == ScreenshotAfterCaptureMode.Clipboard;
            ScreenshotAutoOcrBox.IsChecked = _settings.ScreenshotAutoOcr;
            ScreenshotAutoSaveEnabledBox.IsChecked = _settings.ScreenshotAutoSaveEnabled;
            ScreenshotSaveDirectoryBox.Text = _settings.ScreenshotSaveDirectory;
            ScreenshotFileNameTemplateBox.Text = _settings.ScreenshotFileNameTemplate;
            UpdateScreenshotFileNamePreview();
        }
        finally { _suspendCommit = prev; }
    }

    /// <summary>把当前模板用一组示例参数渲染成"实际产生的文件名"，
    /// 让用户在不真截图的情况下也能立刻看到模板效果。
    /// 示例参数固定为 1920x1080 / 当前 explorer.exe 进程占位，避免预览受真实数据影响</summary>
    private void UpdateScreenshotFileNamePreview()
    {
        try
        {
            var template = ScreenshotFileNameTemplateBox?.Text ?? _settings.ScreenshotFileNameTemplate;
            if (string.IsNullOrWhiteSpace(template))
            {
                if (ScreenshotFileNamePreviewText is not null)
                    ScreenshotFileNamePreviewText.Text = "示例：（模板为空）";
                return;
            }
            var ctx = new ScreenshotAutoSaveContext(1920, 1080, "explorer.exe");
            var name = ScreenshotAutoSaver.ExpandTemplate(template, ctx, _settings.ScreenshotAutoSaveCounter + 1);
            if (ScreenshotFileNamePreviewText is not null)
                ScreenshotFileNamePreviewText.Text = "示例：" + name + ".png";
        }
        catch { }
    }

    private void OnBrowseScreenshotDirClick(object sender, RoutedEventArgs e)
    {
        // 用 .NET 8+ 原生的 Microsoft.Win32.OpenFolderDialog，避免拉 UseWindowsForms 依赖。
        // InitialDirectory 优先指向已有目录，让用户在已有路径基础上微调
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择截图自动保存目录",
            Multiselect = false,
        };
        try
        {
            var current = ScreenshotAutoSaver.ResolveDirectory(_settings.ScreenshotSaveDirectory);
            if (System.IO.Directory.Exists(current)) dialog.InitialDirectory = current;
        }
        catch { }
        if (dialog.ShowDialog(this) == true)
        {
            ScreenshotSaveDirectoryBox.Text = dialog.FolderName;
            CommitAll();
        }
    }

    private void OnOpenScreenshotDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = ScreenshotAutoSaver.ResolveDirectory(_settings.ScreenshotSaveDirectory);
            System.IO.Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ConsoleLog.Instance.Warn("open screenshot dir failed", ("err", ex.Message));
        }
    }

    private void OnOcrResultModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suspendCommit) return;
        var mode = OcrModeQuickRadio.IsChecked == true ? OcrResultMode.Quick : OcrResultMode.Interactive;
        if (_settings.OcrResultMode == mode) return;
        _settings.OcrResultMode = mode;
        _store.SaveSettings(_settings);
    }

    private void OnOcrResultBorderedChanged(object sender, RoutedEventArgs e)
    {
        if (_suspendCommit) return;
        var bordered = OcrResultBorderedToggle.IsChecked == true;
        if (_settings.OcrResultWindowBordered == bordered) return;
        _settings.OcrResultWindowBordered = bordered;
        _store.SaveSettings(_settings);
    }
}

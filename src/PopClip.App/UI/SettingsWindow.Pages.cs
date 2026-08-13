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

    // ============= 对话历史页 =============
    public ObservableCollection<ConversationListItem> HistoryItems { get; } = new();
    public ObservableCollection<UsageRow> UsageItems { get; } = new();

    private void OnHistorySearchChanged(object sender, TextChangedEventArgs e) => RefreshHistoryList();

    private void OnHistoryRefresh(object sender, RoutedEventArgs e) => RefreshHistoryList();

    private void RefreshHistoryList()
    {
        HistoryItems.Clear();
        if (_historyStore is null)
        {
            HistoryListBox.ItemsSource = HistoryItems;
            return;
        }
        var query = HistorySearchBox?.Text?.Trim();
        var list = string.IsNullOrWhiteSpace(query)
            ? _historyStore.Recent(120)
            : _historyStore.Search(query, 120);
        foreach (var s in list)
        {
            HistoryItems.Add(ConversationListItem.From(s));
        }
        HistoryListBox.ItemsSource = HistoryItems;
    }

    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryListBox.SelectedItem is not ConversationListItem item) return;
        _onOpenConversation?.Invoke(item.Id);
    }

    private void OnHistoryDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        if (_historyStore?.Delete(id) == true)
        {
            var existing = HistoryItems.FirstOrDefault(x => x.Id == id);
            if (existing is not null) HistoryItems.Remove(existing);
        }
    }

    // ============= 用量页 =============
    private void RefreshUsageList()
    {
        UsageItems.Clear();
        UsageList.ItemsSource = UsageItems;
        if (_usage is null)
        {
            UsageTotalCalls.Text = UsageTotalPrompt.Text = UsageTotalCompletion.Text = UsageTotalElapsed.Text = "-";
            return;
        }
        var totals = _usage.Totals();
        UsageTotalCalls.Text = totals.Calls.ToString();
        UsageTotalPrompt.Text = FormatTokens(totals.PromptTokens);
        UsageTotalCompletion.Text = FormatTokens(totals.CompletionTokens);
        UsageTotalElapsed.Text = totals.TotalElapsed.TotalSeconds < 60
            ? $"{totals.TotalElapsed.TotalSeconds:0.0}s"
            : $"{totals.TotalElapsed.TotalMinutes:0.0}min";
        foreach (var d in _usage.Daily(30))
        {
            UsageItems.Add(new UsageRow(
                d.Date.ToString("yyyy-MM-dd"),
                $"{d.Calls} 次",
                $"提示 {FormatTokens(d.PromptTokens)}",
                $"补全 {FormatTokens(d.CompletionTokens)}"));
        }
    }

    private static string FormatTokens(int v)
        => v < 10_000 ? v.ToString() : (v / 1000.0).ToString("0.0") + "k";

    private void OnSettingsSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = SettingsSearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchHint.Text = "";
            return;
        }

        var hit = FindSettingsHit(query);
        if (hit is null)
        {
            SearchHint.Text = "未找到匹配设置";
            return;
        }

        NavigateTo(hit.Value.Page);
        SearchHint.Text = hit.Value.Label;
    }

    private void OnSettingsSearchKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var hit = FindSettingsHit(SettingsSearchBox.Text.Trim());
        if (hit is not null) NavigateTo(hit.Value.Page);
    }

    private static (string Page, string Label)? FindSettingsHit(string query)
    {
        var entries = new[]
        {
            ("General", "通用 全屏 开机 自启 托盘 点击 菜单 OCR 剪贴板 暂停 触发 延迟 悬停 修饰键 快捷键"),
            ("Appearance", "外观 主题 深色 浅色 自动 强调色 阴影 边框 圆角 间距 字号"),
            ("Actions", "动作 添加 内置 URL AI Prompt 模板 图标 排序"),
            ("Processes", "进程 过滤 黑名单 白名单 最近活动窗口"),
            ("Search", "搜索 引擎 Google Bing 百度 URL 模板"),
            ("Hotkeys", "快捷键 热键 暂停 恢复 唤起 工具栏"),
            ("Ocr", "OCR 识别 截图 截选 RapidOCR WeChat Interactive Quick 边框 菜单 PrintScreen 延时"),
            ("AI", "AI 模型 Provider DeepSeek OpenAI API Key 自定义 测试连接"),
            ("Templates", "模板 Prompt template 改写 翻译 修语法 commit"),
            ("History", "历史 对话 history 会话 记录"),
            ("Usage", "用量 usage token 调用 统计"),
            ("About", "关于 版本 配置目录"),
        };
        return entries.FirstOrDefault(x => x.Item2.Contains(query, StringComparison.OrdinalIgnoreCase)) is var hit
               && hit.Item1 is not null
            ? (hit.Item1, hit.Item2.Split(' ')[0])
            : null;
    }

    private void SyncPresetSelection()
    {
        var url = _settings.SearchUrlTemplate?.Trim() ?? "";
        var preset = Array.Find(Presets, p => string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase));
        if (preset.Url is null)
        {
            SearchCustom.IsChecked = true;
            return;
        }
        SearchGoogle.IsChecked = preset.Name == "Google";
        SearchBing.IsChecked = preset.Name == "Bing";
        SearchBaidu.IsChecked = preset.Name == "百度";
    }

    private void OnSearchPresetChanged(object sender, RoutedEventArgs e)
    {
        (string Name, string Url)? hit = null;
        if (SearchGoogle.IsChecked == true) hit = Presets[0];
        else if (SearchBing.IsChecked == true) hit = Presets[1];
        else if (SearchBaidu.IsChecked == true) hit = Presets[2];
        if (hit is not null)
        {
            SearchEngineName.Text = hit.Value.Name;
            SearchUrlTemplate.Text = hit.Value.Url;
        }
        // 点击 Custom 不改文本，但仍要触发一次 commit 让 settings 即时落盘
        CommitAll();
    }

    private void OnProcessSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessList.SelectedItem is string process)
        {
            ProcessNameBox.Text = process;
        }
    }

    private void OnAddOrUpdateProcess(object sender, RoutedEventArgs e)
    {
        var process = NormalizeProcessName(ProcessNameBox.Text);
        if (string.IsNullOrWhiteSpace(process)) return;

        var existing = ProcessFilters
            .Select((Value, Index) => (Value, Index))
            .FirstOrDefault(x => string.Equals(x.Value, process, StringComparison.OrdinalIgnoreCase));
        if (existing.Value is not null)
        {
            ProcessFilters[existing.Index] = process;
        }
        else if (ProcessList.SelectedIndex >= 0)
        {
            ProcessFilters[ProcessList.SelectedIndex] = process;
        }
        else
        {
            ProcessFilters.Add(process);
        }
    }

    private void OnRemoveProcess(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is string selected)
        {
            ProcessFilters.Remove(selected);
        }
    }

    private void OnRefreshRecentProcesses(object sender, RoutedEventArgs e) => RefreshRecentProcesses();

    private void RefreshRecentProcesses()
    {
        RecentProcessList.ItemsSource = ForegroundWatcher.RecentProcesses()
            .Where(x => !string.IsNullOrWhiteSpace(x.ProcessName))
            .Select(x => new RecentProcessItem(x.ProcessName, x.WindowTitle))
            .ToList();
    }

    private void OnRecentProcessPicked(object sender, SelectionChangedEventArgs e)
    {
        if (RecentProcessList.SelectedItem is not RecentProcessItem item) return;
        ProcessNameBox.Text = item.ProcessName;
        if (!ProcessFilters.Any(x => string.Equals(x, item.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            ProcessFilters.Add(item.ProcessName);
        }
    }

    private static string NormalizeProcessName(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return "";
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + ".exe";
    }
}

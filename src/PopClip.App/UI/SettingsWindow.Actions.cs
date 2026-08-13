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

    private void OnAddBuiltInAction(object sender, RoutedEventArgs e)
    {
        var alreadyAdded = ActionItems
            .Where(a => string.Equals(a.Type, "builtin", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a.BuiltIn))
            .Select(a => a.BuiltIn!)
            .ToList();

        var dialog = new AddBuiltInActionDialog(alreadyAdded) { Owner = this };
        var ok = dialog.ShowDialog() == true;
        if (!ok || dialog.Selected.Count == 0) return;

        foreach (var seed in dialog.Selected)
        {
            AddAction(new ActionEditorItem
            {
                Id = UniqueActionId(seed.DescriptorId),
                Type = "builtin",
                BuiltIn = seed.BuiltIn,
                Title = seed.Title,
                Icon = seed.IconKey,
                IconLocked = true,
                Enabled = true,
            });
        }
    }

    /// <summary>"添加用户动作"：弹 AddUserActionDialog 让用户选 URL / AI 自定义 / 从内置 AI 模板添加。
    /// 与"添加内置动作"互补：那个对话框管"系统预置 + 不可重复"，本对话框管"用户自定义 + 可重复"，
    /// 旧版独立的"添加 URL"/"添加 AI 动作"/"快速从模板"三个入口合并到这一个对话框</summary>
    private void OnAddUserAction(object sender, RoutedEventArgs e)
    {
        var dialog = new AddUserActionDialog(BuiltinPromptTemplates) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.CreatedItem is null) return;

        var created = dialog.CreatedItem;
        if (string.IsNullOrWhiteSpace(created.Id))
        {
            var seed = string.Equals(created.Type, "url-template", StringComparison.OrdinalIgnoreCase)
                ? "url"
                : "ai";
            created.Id = UniqueActionId(seed);
        }
        else
        {
            created.Id = UniqueActionId(created.Id);
        }
        AddAction(created);
    }

    /// <summary>从内置 Prompt 模板添加 ai 动作。
    /// 模板代表"预定义功能"，图标承载语义，因此 IconLocked=true 不允许后续在 UI 中改图标</summary>
    private void OnAddFromTemplate(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PromptTemplateDefinition tpl) return;
        AddAction(new ActionEditorItem
        {
            Id = UniqueActionId(tpl.Id.Replace("tpl.", "ai-", StringComparison.OrdinalIgnoreCase)),
            Type = "ai",
            Title = tpl.Title,
            Icon = string.IsNullOrWhiteSpace(tpl.Icon) ? "Ai" : tpl.Icon,
            Prompt = tpl.Prompt,
            SystemPrompt = tpl.SystemPrompt,
            OutputMode = string.IsNullOrWhiteSpace(tpl.OutputMode) ? "chat" : tpl.OutputMode,
            IconLocked = true,
            Enabled = true,
        });
    }

    private void AddAction(ActionEditorItem item)
    {
        ActionItems.Add(item);
    }

    private string UniqueActionId(string seed)
    {
        var id = NormalizeIdSeed(seed);
        if (ActionItems.All(x => !string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) return id;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{id}-{i}";
            if (ActionItems.All(x => !string.Equals(x.Id, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
        return $"{id}-{Guid.NewGuid():N}";
    }

    /// <summary>根据内置动作 ID 给出与其语义匹配的图标 key。
    /// 从 BuiltInActionSeeds 取，保持与"添加内置动作"对话框的图标一致</summary>
    private static string SuggestIcon(string builtIn)
    {
        var seed = BuiltInActionSeeds.All.FirstOrDefault(
            s => string.Equals(s.BuiltIn, builtIn, StringComparison.OrdinalIgnoreCase));
        return seed?.IconKey ?? "Ai";
    }

    /// <summary>卡片内"上移"按钮：通过 sender.Tag 拿到目标 item，避免依赖 ListBox 选中状态</summary>
    private void OnActionMoveUp(object sender, RoutedEventArgs e) => MoveAction(SenderItem(sender), -1);
    private void OnActionMoveDown(object sender, RoutedEventArgs e) => MoveAction(SenderItem(sender), 1);

    private void OnActionDelete(object sender, RoutedEventArgs e)
    {
        var item = SenderItem(sender);
        if (item is not null) ActionItems.Remove(item);
    }

    private void MoveAction(ActionEditorItem? item, int delta)
    {
        if (item is null) return;
        var index = ActionItems.IndexOf(item);
        var next = index + delta;
        if (index < 0 || next < 0 || next >= ActionItems.Count) return;
        ActionItems.Move(index, next);
    }

    private static ActionEditorItem? SenderItem(object sender)
        => (sender as FrameworkElement)?.Tag as ActionEditorItem
           ?? (sender as FrameworkElement)?.DataContext as ActionEditorItem;

    /// <summary>拖拽起始：记录起点；命中具体卡片才记录 _dragItem，避免在卡片间空白区起拖</summary>
    private void OnActionDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragItem = FindAncestor<ContentPresenter>((DependencyObject)e.OriginalSource)?.Content as ActionEditorItem;
    }

    private void OnActionMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null) return;
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        // 在输入框内不触发拖拽，避免与文本选择冲突
        if (e.OriginalSource is DependencyObject src && FindAncestor<TextBox>(src) is not null) return;
        DragDrop.DoDragDrop(ActionsList, _dragItem, System.Windows.DragDropEffects.Move);
        _dragItem = null;
    }

    private void OnActionDrop(object sender, WpfDragEventArgs e)
    {
        if (e.Data.GetData(typeof(ActionEditorItem)) is not ActionEditorItem dropped) return;
        var targetPresenter = FindAncestor<ContentPresenter>((DependencyObject)e.OriginalSource);
        var targetItem = targetPresenter?.Content as ActionEditorItem;
        if (targetItem is null || ReferenceEquals(dropped, targetItem)) return;

        var oldIndex = ActionItems.IndexOf(dropped);
        var newIndex = ActionItems.IndexOf(targetItem);
        if (oldIndex < 0 || newIndex < 0) return;
        ActionItems.Move(oldIndex, newIndex);
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T found) return found;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>UniqueActionId 内部的归一化逻辑提炼出来，供"查重 + 生成"两条路径共享。
    /// 入参例如 "tpl.fix-grammar" → "tpl-fix-grammar"</summary>
    private static string NormalizeIdSeed(string seed)
        => seed.Replace("builtin-", "", StringComparison.OrdinalIgnoreCase)
               .Replace("builtin.", "", StringComparison.OrdinalIgnoreCase)
               .Replace(".", "-", StringComparison.Ordinal);

    private void OnActionsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ActionEditorItem item in e.NewItems)
            {
                item.PropertyChanged += OnActionItemChanged;
            }
        }
        if (e.OldItems is not null)
        {
            foreach (ActionEditorItem item in e.OldItems)
            {
                item.PropertyChanged -= OnActionItemChanged;
            }
        }
        CommitAll();
    }
}

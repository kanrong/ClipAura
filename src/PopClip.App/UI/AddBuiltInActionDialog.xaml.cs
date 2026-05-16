using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using PopClip.Actions.BuiltIn;

namespace PopClip.App.UI;

/// <summary>"添加内置动作"对话框。
/// 用途：替代旧的"自动追加第一个未添加动作"按钮，给用户一份按分组排列的待添加内置动作清单：
/// - 已经在 ActionItems 中的内置动作直接被剔除，杜绝重复添加
/// - 多选 + 一次性追加，体验向 macOS PopClip 的"添加扩展"对齐
/// - 分组：基础 / 智能识别 / AI，便于用户理解每类动作的语义边界</summary>
public partial class AddBuiltInActionDialog : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>用户点击"添加"时填入；调用方据此构造 ActionEditorItem 追加到列表</summary>
    public IReadOnlyList<BuiltInActionSeed> Selected { get; private set; } = Array.Empty<BuiltInActionSeed>();

    public ObservableCollection<BuiltInPickGroup> Groups { get; } = new();

    public AddBuiltInActionDialog(IReadOnlyCollection<string> alreadyAdded)
    {
        InitializeComponent();
        DataContext = this;
        var existing = new HashSet<string>(alreadyAdded, StringComparer.OrdinalIgnoreCase);
        var available = BuiltInActionSeeds.All.Where(s => !existing.Contains(s.BuiltIn)).ToList();

        foreach (var group in available
                     .GroupBy(s => s.Group)
                     .OrderBy(g => (int)g.Key))
        {
            var pickGroup = new BuiltInPickGroup(BuiltInActionSeeds.GroupTitle(group.Key));
            foreach (var seed in group)
            {
                var item = new BuiltInPickItem(seed);
                item.PropertyChanged += OnItemChecked;
                pickGroup.Items.Add(item);
            }
            Groups.Add(pickGroup);
        }

        var emptyState = available.Count == 0;
        EmptyHintText.Visibility = emptyState ? Visibility.Visible : Visibility.Collapsed;
        HintText.Visibility = emptyState ? Visibility.Collapsed : Visibility.Visible;
        HintText.Text = $"共 {available.Count} 个可添加动作。智能识别动作在选中对应类型文本时才会出现。";
        ConfirmButton.IsEnabled = false;
        RefreshCount();
    }

    private IEnumerable<BuiltInPickItem> AllItems =>
        Groups.SelectMany(g => g.Items);

    private void OnItemChecked(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BuiltInPickItem.IsChecked)) return;
        RefreshCount();
    }

    private void RefreshCount()
    {
        var count = AllItems.Count(i => i.IsChecked);
        CountText.Text = count == 0 ? "" : $"已选 {count} 个";
        ConfirmButton.IsEnabled = count > 0;
    }

    private void OnToggleSelectAll(object sender, RoutedEventArgs e)
    {
        var items = AllItems.ToList();
        if (items.Count == 0) return;
        var anyUnchecked = items.Any(i => !i.IsChecked);
        foreach (var item in items)
        {
            item.IsChecked = anyUnchecked;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Selected = AllItems.Where(i => i.IsChecked).Select(i => i.Seed).ToList();
        DialogResult = true;
        Close();
    }
}

public sealed class BuiltInPickGroup
{
    public string Title { get; }
    public ObservableCollection<BuiltInPickItem> Items { get; } = new();

    public BuiltInPickGroup(string title) => Title = title;
}

public sealed class BuiltInPickItem : INotifyPropertyChanged
{
    public BuiltInActionSeed Seed { get; }

    public string Title => Seed.Title;
    public string IconKey => Seed.IconKey;
    public string Description => Seed.Description ?? "";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Seed.Description);

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public BuiltInPickItem(BuiltInActionSeed seed) => Seed = seed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

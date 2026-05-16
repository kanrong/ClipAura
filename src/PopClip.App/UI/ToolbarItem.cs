using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PopClip.Core.Actions;

namespace PopClip.App.UI;

/// <summary>浮窗按钮在"分组多行布局"下的归类。
/// 与 BuiltInActionGroup 平行，但运行时仍需要这套独立枚举：
/// AI 模板（type=ai 的用户自定义动作）不在 BuiltInActionSeeds 里，不能从 seed 反查</summary>
public enum ToolbarItemGroup
{
    /// <summary>基础动作（复制 / 粘贴 / 搜索 / 打开链接 / 计算 / 字数 / 翻译 / 剪贴板历史 / OCR 等）</summary>
    Basic,
    /// <summary>智能识别动作（JSON / 颜色 / 时间戳 / 路径 / Markdown 表 / CSV / TSV / ...）</summary>
    Smart,
    /// <summary>AI 动作（含内置 AI 解释 / AI 对话 + 用户自定义 ai 模板派生动作）</summary>
    Ai,
}

/// <summary>工具栏按钮 ViewModel，承载标题/图标键以及点击回调</summary>
public sealed class ToolbarItem : INotifyPropertyChanged
{
    public string Title { get; }
    public string IconKey { get; }
    public ICommand Command { get; }
    public ToolbarItemGroup Group { get; }
    private bool _isKeyboardSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsKeyboardSelected
    {
        get => _isKeyboardSelected;
        set
        {
            if (_isKeyboardSelected == value) return;
            _isKeyboardSelected = value;
            OnPropertyChanged();
        }
    }

    public ToolbarItem(string title, string iconKey, ICommand command, ToolbarItemGroup group = ToolbarItemGroup.Basic)
    {
        Title = title;
        IconKey = iconKey;
        Command = command;
        Group = group;
    }

    public void Invoke()
    {
        if (Command.CanExecute(null)) Command.Execute(null);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>浮窗里"一行按钮"的承载结构。
/// FloatingToolbar 的 XAML 用 ItemsControl(Rows) → 每行内嵌 ItemsControl(row.Items) 实现多行布局。
/// 仅在多行布局模式（SmartOnSeparateRow / GroupRows）下出现 ≥2 个 row，单行模式始终 1 个 row</summary>
public sealed class ToolbarItemRow
{
    public System.Collections.ObjectModel.ObservableCollection<ToolbarItem> Items { get; }
        = new System.Collections.ObjectModel.ObservableCollection<ToolbarItem>();
}

internal sealed class DelegateCommand : ICommand
{
    private readonly Action _execute;
    public DelegateCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

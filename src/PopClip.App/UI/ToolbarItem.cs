using System.Windows.Input;
using PopClip.Core.Actions;

namespace PopClip.App.UI;

/// <summary>工具栏按钮 ViewModel，承载标题/图标键以及点击回调</summary>
public sealed class ToolbarItem
{
    public string Title { get; }
    public string IconKey { get; }
    public ICommand Command { get; }

    public ToolbarItem(string title, string iconKey, ICommand command)
    {
        Title = title;
        IconKey = iconKey;
        Command = command;
    }
}

internal sealed class DelegateCommand : ICommand
{
    private readonly Action _execute;
    public DelegateCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

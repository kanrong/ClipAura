using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PopClip.Core.Actions;

namespace PopClip.App.UI;

/// <summary>工具栏按钮 ViewModel，承载标题/图标键以及点击回调</summary>
public sealed class ToolbarItem : INotifyPropertyChanged
{
    public string Title { get; }
    public string IconKey { get; }
    public ICommand Command { get; }
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

    public ToolbarItem(string title, string iconKey, ICommand command)
    {
        Title = title;
        IconKey = iconKey;
        Command = command;
    }

    public void Invoke()
    {
        if (Command.CanExecute(null)) Command.Execute(null);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class DelegateCommand : ICommand
{
    private readonly Action _execute;
    public DelegateCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

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
    private string _pauseHotKeyStatusText = "尚未检查注册状态";
    private string _toolbarHotKeyStatusText = "尚未检查注册状态";
    private string _ocrHotKeyStatusText = "尚未检查注册状态";
    private string _screenshotHotKeyStatusText = "尚未检查注册状态";
    private string _clipboardHistoryHotKeyStatusText = "尚未检查注册状态";
    private Brush _pauseHotKeyStatusBrush = MakeStatusBrush(HotKeyRegistrationState.Disabled);
    private Brush _toolbarHotKeyStatusBrush = MakeStatusBrush(HotKeyRegistrationState.Disabled);
    private Brush _ocrHotKeyStatusBrush = MakeStatusBrush(HotKeyRegistrationState.Disabled);
    private Brush _screenshotHotKeyStatusBrush = MakeStatusBrush(HotKeyRegistrationState.Disabled);
    private Brush _clipboardHistoryHotKeyStatusBrush = MakeStatusBrush(HotKeyRegistrationState.Disabled);
    public string PauseHotKeyStatusText { get => _pauseHotKeyStatusText; private set => SetField(ref _pauseHotKeyStatusText, value); }
    public string ToolbarHotKeyStatusText { get => _toolbarHotKeyStatusText; private set => SetField(ref _toolbarHotKeyStatusText, value); }
    public string OcrHotKeyStatusText { get => _ocrHotKeyStatusText; private set => SetField(ref _ocrHotKeyStatusText, value); }
    public string ScreenshotHotKeyStatusText { get => _screenshotHotKeyStatusText; private set => SetField(ref _screenshotHotKeyStatusText, value); }
    public string ClipboardHistoryHotKeyStatusText { get => _clipboardHistoryHotKeyStatusText; private set => SetField(ref _clipboardHistoryHotKeyStatusText, value); }
    public Brush PauseHotKeyStatusBrush { get => _pauseHotKeyStatusBrush; private set => SetField(ref _pauseHotKeyStatusBrush, value); }
    public Brush ToolbarHotKeyStatusBrush { get => _toolbarHotKeyStatusBrush; private set => SetField(ref _toolbarHotKeyStatusBrush, value); }
    public Brush OcrHotKeyStatusBrush { get => _ocrHotKeyStatusBrush; private set => SetField(ref _ocrHotKeyStatusBrush, value); }
    public Brush ScreenshotHotKeyStatusBrush { get => _screenshotHotKeyStatusBrush; private set => SetField(ref _screenshotHotKeyStatusBrush, value); }
    public Brush ClipboardHistoryHotKeyStatusBrush { get => _clipboardHistoryHotKeyStatusBrush; private set => SetField(ref _clipboardHistoryHotKeyStatusBrush, value); }

    public void RefreshHotKeyRegistrationStatus()
    {
        var status = _hotKeyStatusProvider?.Invoke();
        if (status is null)
        {
            ApplyHotKeyStatus(
                "尚未检查注册状态", HotKeyRegistrationState.Disabled,
                "尚未检查注册状态", HotKeyRegistrationState.Disabled,
                "尚未检查注册状态", HotKeyRegistrationState.Disabled,
                "尚未检查注册状态", HotKeyRegistrationState.Disabled,
                "尚未检查注册状态", HotKeyRegistrationState.Disabled);
            return;
        }

        ApplyHotKeyStatus(
            status.Pause.Message, status.Pause.State,
            status.Toolbar.Message, status.Toolbar.State,
            status.Ocr.Message, status.Ocr.State,
            status.Screenshot.Message, status.Screenshot.State,
            status.ClipboardHistory.Message, status.ClipboardHistory.State);
    }

    private void ApplyHotKeyStatus(
        string pauseMessage, HotKeyRegistrationState pauseState,
        string toolbarMessage, HotKeyRegistrationState toolbarState,
        string ocrMessage, HotKeyRegistrationState ocrState,
        string screenshotMessage, HotKeyRegistrationState screenshotState,
        string clipboardHistoryMessage, HotKeyRegistrationState clipboardHistoryState)
    {
        PauseHotKeyStatusText = pauseMessage;
        PauseHotKeyStatusBrush = MakeStatusBrush(pauseState);
        ToolbarHotKeyStatusText = toolbarMessage;
        ToolbarHotKeyStatusBrush = MakeStatusBrush(toolbarState);
        OcrHotKeyStatusText = ocrMessage;
        OcrHotKeyStatusBrush = MakeStatusBrush(ocrState);
        ScreenshotHotKeyStatusText = screenshotMessage;
        ScreenshotHotKeyStatusBrush = MakeStatusBrush(screenshotState);
        ClipboardHistoryHotKeyStatusText = clipboardHistoryMessage;
        ClipboardHistoryHotKeyStatusBrush = MakeStatusBrush(clipboardHistoryState);
    }

    private static Brush MakeStatusBrush(HotKeyRegistrationState state)
        => state switch
        {
            HotKeyRegistrationState.Registered => new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x42)),
            HotKeyRegistrationState.Invalid => new SolidColorBrush(Color.FromRgb(0xB5, 0x47, 0x08)),
            HotKeyRegistrationState.Failed => new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18)),
            _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
        };
}

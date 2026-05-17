using System.Threading.Channels;
using System.Diagnostics;
using PopClip.Actions.BuiltIn;
using PopClip.App.Config;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Core.Session;
using PopClip.Hooks;
using PopClip.Hooks.Interop;
using PopClip.Uia;
using PopClip.Uia.Clipboard;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.Hosting;

/// <summary>把 Hooks → 状态机 → 文本获取 → 工具栏弹出 全链路串起来的中枢</summary>
internal sealed class SelectionSessionManager : IDisposable
{
    private static readonly int OwnProcessId = Process.GetCurrentProcess().Id;

    private readonly ILog _log;
    private readonly InputWatcher _watcher;
    private readonly SelectionStateMachine _machine;
    private readonly TextAcquisitionService _acquisition;
    private readonly TextReplacerService _replacer;
    private readonly ActionCatalog _catalog;
    private readonly IActionHost _actionHost;
    private readonly SuppressionGate _gate;
    private readonly FloatingToolbar _toolbar;
    private readonly PauseState _pause;
    private readonly AppSettings _settings;
    private readonly ClipboardAccess _clipboard;
    private readonly ClipboardPaste _pasteInjector;
    private readonly Channel<SelectionCandidate> _candidateChannel;
    private CancellationTokenSource? _cts;

    /// <summary>外部注入的"OCR 截选"触发器；若为 null 则剪贴板启动器中不显示该按钮。
    /// 由 AppHost 在初始化时挂到 OcrCaptureCoordinator.Trigger</summary>
    public Action? OcrLauncher { get; set; }

    /// <summary>外部注入的"剪贴板图片 OCR"触发器；只在剪贴板当前包含图片时显示。</summary>
    public Action<SelectionRect>? ClipboardImageOcrLauncher { get; set; }

    public SelectionSessionManager(
        ILog log,
        InputWatcher watcher,
        TextAcquisitionService acquisition,
        TextReplacerService replacer,
        ActionCatalog catalog,
        IActionHost actionHost,
        SuppressionGate gate,
        FloatingToolbar toolbar,
        PauseState pause,
        AppSettings settings,
        ClipboardAccess clipboard,
        ClipboardPaste pasteInjector)
    {
        _log = log;
        _watcher = watcher;
        _acquisition = acquisition;
        _replacer = replacer;
        _catalog = catalog;
        _actionHost = actionHost;
        _gate = gate;
        _toolbar = toolbar;
        _pause = pause;
        _settings = settings;
        _clipboard = clipboard;
        _pasteInjector = pasteInjector;
        _candidateChannel = Channel.CreateBounded<SelectionCandidate>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _machine = new SelectionStateMachine(log, c => _candidateChannel.Writer.TryWrite(c), CreateSelectionOptions);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => InputPumpAsync(ct));
        _ = Task.Run(() => CandidatePumpAsync(ct));
    }

    private async Task InputPumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var ev in _watcher.Events.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (ev is ForegroundChangedEvent)
                {
                    if (_settings.DismissOnForegroundChanged)
                    {
                        _toolbar.DismissExternal("foreground-changed");
                    }
                }
                else if (ev is MouseDownEvent md)
                {
                    // 单个事件的处理异常不要让 InputPump 整体退出，
                    // 否则后续 ForegroundChanged / 其它 MouseDown 都会丢失，全局鼠标钩子相当于失效
                    try
                    {
                        var isInToolbar = _toolbar.IsShown && _toolbar.ContainsScreenPoint(md.X, md.Y);
                        var isInBubble = AiBubbleWindow.ContainsScreenPoint(md.X, md.Y);
                        if (_toolbar.IsShown && _settings.DismissOnClickOutside && !isInToolbar && !isInBubble)
                        {
                            // 浮窗显示时，点在浮窗外又不在气泡里才关浮窗 —— 气泡是浮窗触发的次级 UI，
                            // 点击它内部不应让浮窗连带消失，避免用户操作气泡时丢失上下文
                            _toolbar.DismissExternal("click-outside");
                        }
                        if (_settings.DismissOnClickOutside
                            && AiBubbleWindow.Current is not null
                            && !isInBubble
                            && !AiBubbleWindow.IsCurrentPinned)
                        {
                            // 点击在气泡外（包括点击在浮窗按钮触发新动作）时关掉旧气泡，避免叠加多张。
                            // Pin 态下用户已经明确表示"不要被自动关掉"，跳过这条规则
                            AiBubbleWindow.DismissCurrent();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Warn 级别只记类型 + 消息（生产环境足够定位），
                        // 完整堆栈走 Debug，避免 Info 级日志被高频鼠标事件污染
                        _log.Warn("mouse-down click-outside check failed", ("err", ex.Message));
                        _log.Debug("mouse-down click-outside detail", ("ex", ex.ToString()));
                    }
                }
                if (IsOwnForegroundInput(ev))
                {
                    continue;
                }
                _machine.Process(ev);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Error("input pump crashed", ex); }
    }

    private async Task CandidatePumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var cand in _candidateChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await ProcessCandidateAsync(cand).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error("candidate processing failed", ex);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessCandidateAsync(SelectionCandidate candidate)
    {
        _log.Info("candidate", ("trigger", candidate.Trigger), ("x", candidate.X), ("y", candidate.Y));

        if (_pause.IsPaused)
        {
            _log.Info("candidate dropped: paused");
            return;
        }

        // 出现新候选即关闭旧浮窗：成功时新浮窗会立即替换，失败时也不会留下"过时"的旧工具栏
        if (_toolbar.IsShown && _settings.DismissOnNewSelection)
        {
            _toolbar.DismissExternal("new-selection");
        }

        var foreground = ForegroundWatcher.Snapshot();
        _log.Info("foreground", ("proc", foreground.ProcessName), ("class", foreground.WindowClassName));

        if (_gate.ShouldSuppress(foreground, out var reason))
        {
            _log.Info("suppressed", ("reason", reason), ("proc", foreground.ProcessName));
            return;
        }

        var mouseRect = ResolveAnchorRect(candidate);

        // 修饰键 + Click：用户明确表达"想操作剪贴板"，跳过文本采集
        if (candidate.Trigger == SelectionTrigger.MouseModifierClick)
        {
            await ShowClipboardLauncherAsync(foreground, mouseRect).ConfigureAwait(false);
            return;
        }

        var attempt = await Task.Run(() => _acquisition.Acquire(foreground, mouseRect, candidate.Trigger, candidate.IsLikelyWindowDrag, candidate.IsLikelyScrollBarDrag)).ConfigureAwait(false);
        var outcome = attempt.Outcome;
        if (outcome is null)
        {
            if (!attempt.WasSkipped)
            {
                _log.Info("acquisition failed: no text from UIA nor clipboard");
            }
            return;
        }
        if (candidate.Trigger == SelectionTrigger.MouseDoubleClick
            && outcome.Context.Source == AcquisitionSource.ClipboardFallback)
        {
            _log.Info("double-click clipboard fallback captured",
                ("proc", outcome.Context.Foreground.ProcessName),
                ("class", outcome.Context.Foreground.WindowClassName),
                ("focusedClass", outcome.FocusedWindowClassName),
                ("controlType", outcome.FocusedControlTypeName),
                ("len", outcome.Context.Text.Length),
                ("head", BuildLogHead(outcome.Context.Text)));
        }
        if (outcome.Context.IsEmpty)
        {
            _log.Info("acquisition empty text", ("source", outcome.Context.Source));
            return;
        }
        if (outcome.Context.Text.Length < _settings.MinTextLength || outcome.Context.Text.Length > _settings.MaxTextLength)
        {
            _log.Info("acquisition dropped by length",
                ("len", outcome.Context.Text.Length),
                ("min", _settings.MinTextLength),
                ("max", _settings.MaxTextLength));
            return;
        }

        _log.Info("acquired",
            ("trigger", candidate.Trigger),
            ("source", outcome.Context.Source),
            ("proc", outcome.Context.Foreground.ProcessName),
            ("class", outcome.Context.Foreground.WindowClassName),
            ("focusedClass", outcome.FocusedWindowClassName),
            ("controlType", outcome.FocusedControlTypeName),
            ("len", outcome.Context.Text.Length),
            ("editable", outcome.Context.IsLikelyEditable));

        _replacer.SetCurrentElement(outcome.Element);

        var visible = _catalog.GetVisible(outcome.Context)
            .Where(v => !IsAiAction(v.Action) || _actionHost.Ai.CanRun)
            // ExplainActionEnabled 是一个"独立可关"的细分开关：即使 AI 已启用，
            // 仍允许用户隐藏"AI 解释"按钮以保持浮窗精简，不影响其它 AI 动作
            .Where(v => !string.Equals(v.Action.Id, BuiltInActionIds.AiExplain, StringComparison.OrdinalIgnoreCase)
                        || _settings.ExplainActionEnabled)
            .ToList();
        _log.Info("visible actions", ("count", visible.Count));
        if (visible.Count == 0) return;

        var items = visible.Select(v =>
        {
            // 搜索按钮跟随设置中的引擎名动态显示，无需在 actions.json 里手动改 title
            var title = v.Action.Id == BuiltInActionIds.Search
                ? _settings.SearchEngineName
                : v.Descriptor.Title.Length > 0 ? v.Descriptor.Title : v.Action.Title;
            var icon = !string.IsNullOrEmpty(v.Descriptor.Icon) ? v.Descriptor.Icon : v.Action.IconKey;
            var group = ResolveToolbarGroup(v.Descriptor);
            return new ToolbarItem(title, icon, new DelegateCommand(() => RunAction(v.Action, outcome.Context, title, v.Descriptor)), group);
        }).ToList();

        await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
        {
            _toolbar.ApplyAppearance(_settings);
            _toolbar.ApplyItems(items, _settings.ToolbarLayoutMode);
            _toolbar.ShowAt(mouseRect, outcome.Context.Foreground);
        });
    }

    /// <summary>ActionDescriptor → ToolbarItemGroup 的统一转换。
    /// 在浮窗布局模式下决定按钮归到哪一行（基础 / 智能 / AI）。
    /// AI 模板（type=ai）一律归 AI 组；内置动作按 BuiltInActionSeeds 反查；其它（type=url-template 等）归 Basic</summary>
    private static ToolbarItemGroup ResolveToolbarGroup(ActionDescriptor descriptor)
    {
        if (string.Equals(descriptor.Type, "ai", StringComparison.OrdinalIgnoreCase))
            return ToolbarItemGroup.Ai;
        if (string.Equals(descriptor.Type, "builtin", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(descriptor.BuiltIn))
        {
            return BuiltInActionSeeds.GroupOf(descriptor.BuiltIn) switch
            {
                BuiltInActionGroup.Smart => ToolbarItemGroup.Smart,
                BuiltInActionGroup.Ai => ToolbarItemGroup.Ai,
                _ => ToolbarItemGroup.Basic,
            };
        }
        return ToolbarItemGroup.Basic;
    }

    /// <summary>修饰键 + Click 触发的简化工具条：暴露粘贴与剪贴板历史入口</summary>
    private async Task ShowClipboardLauncherAsync(ForegroundWindowInfo foreground, SelectionRect mouseRect)
    {
        var items = new List<ToolbarItem>();
        var hwnd = foreground.Hwnd;
        if (HasClipboardText())
        {
            items.Add(new ToolbarItem("粘贴", "Paste", new DelegateCommand(() =>
            {
                // 必须先关闭浮窗（释放焦点状态），再 SetForegroundWindow + Ctrl+V
                _toolbar.DismissExternal("paste-invoked");
                _ = Task.Run(() =>
                {
                    try { _pasteInjector.PasteCurrent(hwnd); }
                    catch (Exception ex) { _log.Error("paste failed", ex); }
                });
            })));
        }

        if (ClipboardImageOcrLauncher is not null && HasClipboardImage())
        {
            items.Add(new ToolbarItem("图片 OCR", "OcrImage", new DelegateCommand(() =>
            {
                _toolbar.DismissExternal("clipboard-image-ocr-invoked");
                try { ClipboardImageOcrLauncher?.Invoke(mouseRect); }
                catch (Exception ex) { _log.Warn("clipboard image ocr launcher failed", ("err", ex.Message)); }
            })));
        }

        if (_actionHost.ClipboardHistory is not null)
        {
            var anchor = new SelectionContext(
                "",
                AcquisitionSource.Unknown,
                foreground,
                mouseRect,
                IsLikelyEditable: true,
                DateTime.UtcNow);
            items.Add(new ToolbarItem("剪贴板", "ClipboardHistory", new DelegateCommand(() =>
            {
                _toolbar.DismissExternal("clipboard-history-invoked");
                _replacer.SetCurrentElement(null);
                _actionHost.ClipboardHistory.Open(anchor);
            })));
        }

        if (OcrLauncher is not null)
        {
            // OCR 入口仅在剪贴板启动器里出现（不进正常选区流程），让用户从一个统一的"修饰键+点击"汇集点访问
            items.Add(new ToolbarItem("OCR 截选", "Ocr", new DelegateCommand(() =>
            {
                _toolbar.DismissExternal("ocr-invoked");
                try { OcrLauncher?.Invoke(); }
                catch (Exception ex) { _log.Warn("ocr launcher failed", ("err", ex.Message)); }
            })));
        }

        if (items.Count == 0)
        {
            _log.Info("modifier-click ignored: no clipboard actions available");
            return;
        }

        await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
        {
            _toolbar.ApplyAppearance(_settings);
            _toolbar.ApplyItems(items, _settings.ToolbarLayoutMode);
            _toolbar.ShowAt(mouseRect, foreground);
        });
    }

    private bool HasClipboardText()
    {
        try { return _clipboard.HasText(); }
        catch (Exception ex)
        {
            _log.Warn("modifier-click clipboard check failed", ("err", ex.Message));
            return false;
        }
    }

    private bool HasClipboardImage()
    {
        try { return _clipboard.HasImage(); }
        catch (Exception ex)
        {
            _log.Warn("modifier-click clipboard image check failed", ("err", ex.Message));
            return false;
        }
    }

    private static bool IsOwnForegroundInput(InputEvent ev)
    {
        if (ev is not MouseDownEvent
            && ev is not MouseUpEvent
            && ev is not MouseMoveEvent
            && ev is not KeyEvent)
        {
            return false;
        }

        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == OwnProcessId;
    }

    public void ShowLauncherAtCursor()
    {
        if (_pause.IsPaused) return;
        var foreground = ForegroundWatcher.Snapshot();
        if (!NativeMethods.GetCursorPos(out var pt))
        {
            pt = new NativeMethods.POINT { X = 0, Y = 0 };
        }
        _ = ShowClipboardLauncherAsync(foreground, SelectionRect.FromPoint(pt.X, pt.Y));
    }

    /// <summary>外部采集到文本（目前仅 OCR）后调用，跳过 UIA / 剪贴板兜底，
    /// 直接复用浮窗 + 动作链路。anchorRect 应给出截图框的物理像素矩形，
    /// 浮窗会以它的左下作为基准定位（与正常选区一致）。
    ///
    /// IsLikelyEditable 永远 false：OCR 来源无法回写源应用，"替换/插入"等动作由 IActionHost 的可见性逻辑自动屏蔽</summary>
    public void ShowToolbarForExternalText(string text, SelectionRect anchorRect, AcquisitionSource source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            WpfApplication.Current.Dispatcher.Invoke(() =>
                _toolbar.ShowInlineToast("OCR 未识别到文本", isError: true));
            return;
        }
        if (_pause.IsPaused)
        {
            _log.Info("external text dropped: paused");
            return;
        }

        var foreground = ForegroundWatcher.Snapshot();
        if (_gate.ShouldSuppress(foreground, out var reason))
        {
            _log.Info("external text suppressed", ("reason", reason), ("source", source));
            return;
        }

        var ctx = new SelectionContext(
            text.Trim(),
            source,
            foreground,
            anchorRect,
            IsLikelyEditable: false,
            DateTime.UtcNow);

        // OCR 路径没有 UIA element 可供 TextPattern 回写，主动清空，避免上一次的 element 被错误复用
        _replacer.SetCurrentElement(null);

        var visible = _catalog.GetVisible(ctx)
            .Where(v => !IsAiAction(v.Action) || _actionHost.Ai.CanRun)
            .Where(v => !string.Equals(v.Action.Id, BuiltInActionIds.AiExplain, StringComparison.OrdinalIgnoreCase)
                        || _settings.ExplainActionEnabled)
            .ToList();
        _log.Info("external text visible actions", ("count", visible.Count), ("source", source));
        if (visible.Count == 0)
        {
            WpfApplication.Current.Dispatcher.Invoke(() =>
                _toolbar.ShowInlineToast("没有可用动作", isError: true));
            return;
        }

        var items = visible.Select(v =>
        {
            var title = v.Action.Id == BuiltInActionIds.Search
                ? _settings.SearchEngineName
                : v.Descriptor.Title.Length > 0 ? v.Descriptor.Title : v.Action.Title;
            var icon = !string.IsNullOrEmpty(v.Descriptor.Icon) ? v.Descriptor.Icon : v.Action.IconKey;
            var group = ResolveToolbarGroup(v.Descriptor);
            return new ToolbarItem(title, icon, new DelegateCommand(() => RunAction(v.Action, ctx, title, v.Descriptor)), group);
        }).ToList();

        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            _toolbar.ApplyAppearance(_settings);
            _toolbar.ApplyItems(items, _settings.ToolbarLayoutMode);
            _toolbar.ShowAt(anchorRect, foreground);
        });
    }

    private SelectionRect ResolveAnchorRect(SelectionCandidate candidate)
    {
        if (candidate.X >= 0 && candidate.Y >= 0)
        {
            return SelectionRect.FromPoint(candidate.X, candidate.Y);
        }

        // 键盘触发没有鼠标 hook 坐标，显示位置改用当前 cursor 物理像素坐标。
        // 避免把 (-1, -1) 交给 MonitorFromPoint 后在多显示器环境贴到错误屏幕边角。
        if (NativeMethods.GetCursorPos(out var pt))
        {
            _log.Debug("candidate anchor resolved from cursor",
                ("trigger", candidate.Trigger),
                ("x", pt.X),
                ("y", pt.Y));
            return SelectionRect.FromPoint(pt.X, pt.Y);
        }

        _log.Warn("candidate anchor unavailable; falling back to origin",
            ("trigger", candidate.Trigger),
            ("x", candidate.X),
            ("y", candidate.Y));
        return SelectionRect.FromPoint(0, 0);
    }

    private SelectionStateOptions CreateSelectionOptions()
    {
        return new SelectionStateOptions
        {
            PopupMode = _settings.PopupMode,
            PopupDelayMs = _settings.PopupDelayMs,
            HoverDelayMs = _settings.HoverDelayMs,
            RequiredModifier = _settings.RequiredModifier,
            QuickClickModifier = _settings.QuickClickModifier,
            EnableSelectAllPopup = _settings.EnableSelectAllPopup,
        };
    }

    private void RunAction(IAction action, SelectionContext context, string title, ActionDescriptor? descriptor = null)
    {
        var toastBefore = _toolbar.LastToastAtUtc;
        var isAiAction = IsAiAction(action);
        // 注入 descriptor 上下文，让智能动作可读 host.Descriptor.OutputMode 决定输出落点
        var scopedHost = new ScopedActionHost(_actionHost, descriptor);
        _ = Task.Run(async () =>
        {
            try
            {
                // AI 动作的"处理中"提示交给 AI 服务自身负责：
                // - bubble 模式（翻译/解释）在创建 AiBubbleWindow 时显示"请求中…"状态
                // - 非 bubble 模式（replace/clipboard/inlineToast）由 AiTextService.RunInlineAsync
                //   主动发 Notify("处理中...")
                // 这里不再统一弹长 6 秒 toast，避免在气泡正中央反复覆盖结果视线
                var timeoutSeconds = isAiAction
                    ? Math.Clamp(_settings.AiTimeoutSeconds + 15, 20, 240)
                    : 15;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                await action.RunAsync(context, scopedHost, cts.Token).ConfigureAwait(false);
                await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
                {
                    // toast 判定：动作执行后用户已经能"看见"结果的（气泡 / 对话窗 / 原地替换 / 自身已 Notify）
                    // 一律不补 toast，避免遮挡结果或与气泡叠加；只有"仅复制"这种纯后台动作才需要补 ✓ 提示
                    if (ShouldShowCompletionToast(action, descriptor) && _toolbar.LastToastAtUtc <= toastBefore)
                    {
                        var text = action.Id == BuiltInActionIds.Copy ? "已复制 ✓" : $"{title} ✓";
                        _toolbar.ShowInlineToast(text);
                    }
                });
                await Task.Delay(700).ConfigureAwait(false);
                if (_settings.DismissOnActionInvoked)
                {
                    _toolbar.DismissExternal("action-completed");
                }
            }
            catch (Exception ex)
            {
                _log.Error("action run failed", ex, ("id", action.Id));
                await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
                {
                    _toolbar.ShowInlineToast("失败：" + ex.Message, isError: true, copyText: ex.ToString(), durationMs: 5000);
                });
            }
        });
    }

    /// <summary>判定一个动作是否走 AI 路径。
    /// 仅影响超时窗口长度（AI 调用慢，给 AiTimeoutSeconds+15 的余量）。
    /// toast 是否补、bubble 是否处理 都不再依赖此判定 —— 那些由 ShouldShowCompletionToast 单独决定</summary>
    private bool IsAiAction(IAction action)
    {
        if (action is AiPromptAction) return true;
        if (action.Id.StartsWith("builtin.ai.", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(action.Id, BuiltInActionIds.Translate, StringComparison.OrdinalIgnoreCase)
            && _actionHost.Ai.CanRun
            && _settings.TranslateInlineWhenAiEnabled)
        {
            return true;
        }
        return false;
    }

    /// <summary>决定动作完成后是否补一个轻量"✓" toast。
    /// 规则：只有当结果用户感知不到（纯剪贴板 / 静默写入）时才补 toast；
    /// 一旦动作产出可见 UI（气泡 / 对话窗 / 原地替换 / inline toast）就保持安静，避免遮挡结果。
    ///
    /// 判定依据是 descriptor.OutputMode 字符串：
    /// - 内置智能动作：BuiltInOutputMode 的 Bubble / CopyAndBubble / Dialog → 安静
    /// - AI 动作：chat（独立对话窗）/ replace（原地）/ inlineToast（自身已 Notify） → 安静
    /// - 其余（Copy / clipboard / 缺省）→ 补 toast，告诉用户"动作已执行"
    ///
    /// 特例：内置 Translate 在 AI 启用且开启内联翻译时会走 AI 气泡，
    /// 但它的 descriptor.OutputMode 不会被填成 AI 那套（descriptor 是 Translate 自己的），
    /// 所以单独按动作 id 判定一次</summary>
    private bool ShouldShowCompletionToast(IAction action, ActionDescriptor? descriptor)
    {
        if (IsSilentOutputMode(descriptor?.OutputMode))
        {
            return false;
        }
        if (string.Equals(action.Id, BuiltInActionIds.Translate, StringComparison.OrdinalIgnoreCase)
            && _actionHost.Ai.CanRun
            && _settings.TranslateInlineWhenAiEnabled)
        {
            return false;
        }
        return true;
    }

    private static bool IsSilentOutputMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return false;
        var trimmed = mode.Trim();
        return trimmed.Equals("Bubble", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("CopyAndBubble", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Dialog", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("chat", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("replace", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("inlineToast", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("ai-bubble", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildLogHead(string text)
    {
        var collapsed = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (string.IsNullOrEmpty(collapsed)) return "";

        const int maxLen = 200;
        return collapsed.Length <= maxLen
            ? collapsed
            : collapsed[..maxLen] + "…";
    }

    public void Dispose()
    {
        _cts?.Cancel();
    }
}

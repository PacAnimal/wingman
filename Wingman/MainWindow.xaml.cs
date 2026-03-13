using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EasyWindowsTerminalControl;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Terminal.Wpf;

namespace Wingman;

public partial class MainWindow
{
    private static readonly SolidColorBrush FocusBorderBrush = new(Color.FromRgb(0x4A, 0x67, 0x85));

    private readonly ILoggerFactory _loggerFactory;
    private readonly IWindowsNative _native;
    private readonly ISettingsService _settings;
    private readonly AiProviderKind? _initialProvider;
    private readonly List<TabSession> _tabs = [];
    private readonly HashSet<Guid> _workingWhenLeft = [];
    private TabSession? _activeTab;
    private readonly List<TabSession> _spareTabs = [];
    private readonly SemaphoreSlim _spareInitLock = new(1, 1);
    private readonly HashSet<Guid> _highlightedTabs = [];
    private int _spareGeneration;
    private bool _firstTabReady;
    private FocusTarget _focusBeforeCard;
    private AiProviderKind? _pendingProviderConstraint;
    private string? _startupError;
    private TerminalTheme? _terminalTheme;

    public MainWindow(ILoggerFactory loggerFactory, IWindowsNative native, ISettingsService settings, string? startupError, AiProviderKind? initialProvider)
    {
        _loggerFactory = loggerFactory;
        _native = native;
        _settings = settings;
        _initialProvider = initialProvider;
        _startupError = startupError;
        InitializeComponent();

        TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();

        if (!_native.ProbeConPTY())
            MessageBox.Show("FAILED to load conpty.dll — ConPTY will not work.",
                "Missing Native DLL", MessageBoxButton.OK, MessageBoxImage.Error);

        // Ctrl+C hook references active tab dynamically
        _native.HookPreprocessMessage(
            () => _activeTab?.ChatPanel.CurrentFocus == FocusTarget.ChatLogText,
            () => _activeTab?.TerminalControl.Terminal?.GetSelectedText(),
            () => { if (_activeTab != null) Dispatcher.BeginInvoke(() => _activeTab.TerminalControl.Focus()); });
        Closed += (_, _) => _native.UnhookPreprocessMessage();

        // TabBar events
        TabBar.TabSelected += id =>
        {
            var tab = _tabs.Find(t => t.Id == id);
            if (tab != null) SwitchToTab(tab);
        };
        TabBar.TabCloseRequested += id =>
        {
            var tab = _tabs.Find(t => t.Id == id);
            if (tab != null) CloseTab(tab);
        };
        TabBar.NewTabRequested += () => _ = CreateTab();
        TabBar.TabRenamed += (id, newTitle) =>
        {
            var tab = _tabs.Find(t => t.Id == id);
            if (tab != null)
            {
                tab.Title = newTitle;
                tab.IsManuallyNamed = true;
                if (tab == _activeTab)
                    UpdateTitle();
            }
        };

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseUp += (_, _) =>
        {
            if (_activeTab?.ChatPanel.HasActiveCard == true)
                Dispatcher.BeginInvoke(_activeTab.ChatPanel.FocusActiveCard, System.Windows.Threading.DispatcherPriority.Input);
        };
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
        Loaded += (_, _) => _activeTab?.ChatPanel.FocusPrimaryInput();
        Closing += (_, _) => Hide();
        Deactivated += (_, _) => { if (_highlightedTabs.Count > 0) _native.FlashWindow(this); };

        // Create first tab synchronously — Init must disconnect ConPTY before Show/Loaded fires
        var firstTab = CreateTabSession();
        _tabs.Add(firstTab);
        TabBar.AddTab(firstTab.Id, firstTab.Title);
        ContentGrid.Children.Add(firstTab.ChatPanel);
        ContentGrid.Children.Add(firstTab.TerminalBorder);
        _ = InitTabTerminal(firstTab);
        _activeTab = firstTab;
        TabBar.SetActiveTab(firstTab.Id);
    }

    private async Task InitTabTerminal(TabSession tab)
    {
        await tab.Terminal.Init(tab.TerminalControl);
        if (!tab.TerminalControl.IsKeyboardFocusWithin)
            tab.TerminalControl.IsCursorVisible = false;

        _ = tab.Terminal.RunCommand("clear; Write-Host \"`nWingman ready!`n\" -ForegroundColor Green");

        _firstTabReady = true;
        _ = InitSpareTab();
        _ = InitSpareTab();
    }

    private Task CreateTab()
    {
        if (_spareTabs.Count > 0)
        {
            var spare = _spareTabs[0];
            _spareTabs.RemoveAt(0);
            _tabs.Add(spare);
            TabBar.AddTab(spare.Id, spare.Title);
            SwitchToTab(spare);
            // clear+welcome runs here (not during init) so the renderer is already correctly sized
            _ = spare.Terminal.RunCommand("clear; Write-Host \"`nWingman ready!`n\" -ForegroundColor Green");
            _ = InitSpareTab();
            return Task.CompletedTask;
        }
        return CreateTabFresh();
    }

    private async Task CreateTabFresh()
    {
        var tab = CreateTabSession();
        _tabs.Add(tab);
        TabBar.AddTab(tab.Id, tab.Title);

        // hide until SwitchToTab — prevents overlap with current tab
        tab.ChatPanel.Visibility = Visibility.Hidden;
        tab.TerminalBorder.Visibility = Visibility.Hidden;
        ContentGrid.Children.Add(tab.ChatPanel);
        ContentGrid.Children.Add(tab.TerminalBorder);

        try
        {
            var cols = _activeTab?.TerminalControl.Terminal?.Columns ?? 80;
            var rows = _activeTab?.TerminalControl.Terminal?.Rows ?? 24;
            await tab.Terminal.Init(tab.TerminalControl, cols, rows);
            if (_terminalTheme != null)
                tab.TerminalControl.Theme = _terminalTheme;
            if (!tab.TerminalControl.IsKeyboardFocusWithin)
                tab.TerminalControl.IsCursorVisible = false;

            var stored = await _settings.LoadAsync();
            var apiKey = stored.EffectiveApiKey;
            if (!string.IsNullOrEmpty(apiKey))
                await ActivateAi(apiKey, tab);

            SwitchToTab(tab);
            // clear+welcome runs here (not during init) so the renderer is already correctly sized
            _ = tab.Terminal.RunCommand("clear; Write-Host \"`nWingman ready!`n\" -ForegroundColor Green");
            _ = InitSpareTab();
        }
        catch
        {
            _tabs.Remove(tab);
            TabBar.RemoveTab(tab.Id);
            ContentGrid.Children.Remove(tab.ChatPanel);
            ContentGrid.Children.Remove(tab.TerminalBorder);
            tab.Dispose();
        }
    }

    private async Task InitSpareTab()
    {
        var gen = _spareGeneration;
        await _spareInitLock.WaitAsync();
        TabSession? tab = null;
        try
        {
            if (_spareGeneration != gen || _spareTabs.Count >= 2) return;

            tab = CreateTabSession();
            tab.ChatPanel.Visibility = Visibility.Hidden;
            tab.TerminalBorder.Visibility = Visibility.Hidden;
            ContentGrid.Children.Add(tab.ChatPanel);
            ContentGrid.Children.Add(tab.TerminalBorder);

            var cols = _activeTab?.TerminalControl.Terminal?.Columns ?? 80;
            var rows = _activeTab?.TerminalControl.Terminal?.Rows ?? 24;
            await tab.Terminal.Init(tab.TerminalControl, cols, rows);
            if (_spareGeneration != gen) return;

            if (_terminalTheme != null)
                tab.TerminalControl.Theme = _terminalTheme;
            if (!tab.TerminalControl.IsKeyboardFocusWithin)
                tab.TerminalControl.IsCursorVisible = false;

            var stored = await _settings.LoadAsync();
            if (_spareGeneration != gen) return;

            var apiKey = stored.EffectiveApiKey;
            if (!string.IsNullOrEmpty(apiKey))
                await ActivateAi(apiKey, tab);
            if (_spareGeneration != gen) return;

            if (_spareTabs.Count < 2)
            {
                _spareTabs.Add(tab);
                tab = null; // ownership transferred
            }
        }
        finally
        {
            _spareInitLock.Release();
            if (tab != null)
            {
                ContentGrid.Children.Remove(tab.ChatPanel);
                ContentGrid.Children.Remove(tab.TerminalBorder);
                tab.Dispose();
            }
        }
    }

    private void DisposeSpares()
    {
        _spareGeneration++; // abort in-flight inits
        foreach (var spare in _spareTabs)
        {
            ContentGrid.Children.Remove(spare.ChatPanel);
            ContentGrid.Children.Remove(spare.TerminalBorder);
            spare.Dispose();
        }
        _spareTabs.Clear();
    }

    private TabSession CreateTabSession()
    {
        var tab = new TabSession(_loggerFactory);

        tab.ChatPanel.Initialize(null, null, _startupError, OnApiKeySubmitted);
        tab.ChatPanel.ResetRequested += async () =>
        {
            await tab.Terminal.Reset();
            tab.TaskDescription?.Reset();
        };
        tab.ChatPanel.CardActiveChanged += cardActive =>
        {
            if (tab != _activeTab) return;
            if (cardActive)
            {
                _focusBeforeCard = tab.ChatPanel.CurrentFocus;
                tab.ChatPanel.CurrentFocus = FocusTarget.QuestionCard;
                tab.TerminalBorder.BorderBrush = Brushes.Transparent;
            }
            else
            {
                if (_focusBeforeCard == FocusTarget.Console)
                    tab.TerminalControl.Focus();
                else
                    tab.ChatPanel.FocusPrimaryInput();
            }
        };

        tab.Terminal.ProcessExited += () => Dispatcher.BeginInvoke(() => CloseTab(tab));
        tab.Terminal.CommandCompleted += () =>
        {
            if (tab != _activeTab) return;
            if (tab.ChatPanel.CurrentFocus != FocusTarget.Console)
                Dispatcher.BeginInvoke(() => tab.TerminalControl.IsCursorVisible = false);
        };

        tab.TerminalControl.GotFocus += (_, _) =>
        {
            tab.ChatPanel.CurrentFocus = FocusTarget.Console;
            tab.LastFocus = FocusTarget.Console;
            tab.TerminalControl.IsCursorVisible = true;
            tab.TerminalBorder.BorderBrush = FocusBorderBrush;
        };
        tab.TerminalControl.LostFocus += (_, _) =>
        {
            tab.TerminalControl.IsCursorVisible = false;
            tab.TerminalBorder.BorderBrush = Brushes.Transparent;
        };

        tab.Spinner = new TitleSpinner(frame =>
        {
            var tabTitle = tab.IsManuallyNamed ? tab.Title
                : tab.TaskDescription?.CurrentTask ?? (tab.Title != "New Tab" ? tab.Title : null);
            TabBar.UpdateTitle(tab.Id, tabTitle ?? tab.Title, frame);

            if (tab == _activeTab)
                UpdateTitle();
            else
                UpdateTaskbar();
        });

        return tab;
    }

    private void SwitchToTab(TabSession tab)
    {
        if (_activeTab == tab) return;

        if (_activeTab != null)
        {
            _activeTab.LastFocus = _activeTab.ChatPanel.CurrentFocus;
            _activeTab.ChatPanel.Visibility = Visibility.Hidden;
            _activeTab.TerminalBorder.Visibility = Visibility.Hidden;
            if (_activeTab.Spinner?.CurrentFrame != null)
                _workingWhenLeft.Add(_activeTab.Id);
            else
                _workingWhenLeft.Remove(_activeTab.Id);
        }

        _activeTab = tab;
        TabBar.ClearHighlight(tab.Id);
        _highlightedTabs.Remove(tab.Id);
        _workingWhenLeft.Remove(tab.Id);
        tab.ChatPanel.Visibility = Visibility.Visible;
        tab.TerminalBorder.Visibility = Visibility.Visible;

        TabBar.SetActiveTab(tab.Id);
        UpdateTitle();

        Dispatcher.BeginInvoke(() =>
        {
            if (tab.LastFocus == FocusTarget.Console)
                tab.TerminalControl.Focus();
            else
                tab.ChatPanel.FocusPrimaryInput();
        });
    }

    private void CloseTab(TabSession tab)
    {
        if (_spareTabs.Contains(tab))
        {
            ContentGrid.Children.Remove(tab.ChatPanel);
            ContentGrid.Children.Remove(tab.TerminalBorder);
            tab.Dispose();
            _spareTabs.Remove(tab);
            _ = InitSpareTab();
            return;
        }

        // Guard: ProcessExited fires after Dispose, re-entering CloseTab for an already-removed tab
        if (!_tabs.Contains(tab)) return;

        if (_tabs.Count <= 1)
        {
            Close();
            return;
        }

        if (tab.ChatPanel.IsStreaming)
            tab.ChatPanel.CancelStreaming();

        if (tab.Terminal.IsCommandRunning)
            tab.Terminal.SendCtrlC();

        ContentGrid.Children.Remove(tab.ChatPanel);
        ContentGrid.Children.Remove(tab.TerminalBorder);

        var idx = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        _highlightedTabs.Remove(tab.Id);
        _workingWhenLeft.Remove(tab.Id);
        TabBar.RemoveTab(tab.Id);

        if (_activeTab == tab)
        {
            _activeTab = null;
            var newIdx = Math.Min(idx, _tabs.Count - 1);
            SwitchToTab(_tabs[newIdx]);
        }

        tab.Dispose();
    }

    private string _currentTitle = "Wingman";

    private void UpdateTitle()
    {
        if (_activeTab == null) return;
        var task = _activeTab.TaskDescription?.CurrentTask;
        var tabTitle = _activeTab.IsManuallyNamed ? _activeTab.Title
            : task ?? (_activeTab.Title != "New Tab" ? _activeTab.Title : null);

        // no spinner in Window.Title — SetWindowText triggers DWM recomposition that flashes the terminal
        var newTitle = tabTitle != null ? $"Wingman - {tabTitle}" : "Wingman";
        if (newTitle != _currentTitle)
        {
            _currentTitle = newTitle;
            Title = newTitle;
        }

        UpdateTaskbar();
    }

    private void UpdateTaskbar()
    {
        TaskbarItemInfo.ProgressState = _tabs.Any(t => t.Spinner?.CurrentFrame != null)
            ? System.Windows.Shell.TaskbarItemProgressState.Indeterminate
            : System.Windows.Shell.TaskbarItemProgressState.None;
    }

    private async Task<string?> OnApiKeySubmitted(string key)
    {
        if (_pendingProviderConstraint is { } expected)
        {
            var detected = AiProvider.Detect(key).Kind;
            if (detected != expected)
            {
                var expectedLabel = expected == AiProviderKind.OpenAi ? "OpenAI" : "Anthropic";
                var detectedLabel = detected == AiProviderKind.OpenAi ? "OpenAI" : "Anthropic";
                return $"That key looks like {detectedLabel}, not {expectedLabel}.";
            }
        }

        var error = await _settings.ValidateKeyAsync(key);
        if (error != null) return error;
        await _settings.SaveKeyAsync(key);
        _pendingProviderConstraint = null;
        await ActivateAi(key);
        UpdateProviderCheckmark(AiProvider.Detect(key).Kind);
        return null;
    }

    internal async Task ActivateAi(string apiKey)
    {
        _startupError = null;
        DisposeSpares();
        foreach (var tab in _tabs)
            await ActivateAi(apiKey, tab);
        if (_firstTabReady)
        {
            _ = InitSpareTab();
            _ = InitSpareTab();
        }
    }

    private async Task ActivateAi(string apiKey, TabSession tab)
    {
        var provider = AiProvider.Detect(apiKey);

        var guardClient = provider.CreateGuardClient(apiKey);
        var guard = new CommandGuard(guardClient, _loggerFactory.CreateLogger<CommandGuard>());

        var memory = new MemoryService(_settings);
        var memoryBlock = await memory.FormatForSystemPrompt();

        var events = new AgentEvents();
        tab.Events = events;
        var approvalUi = new ApprovalUi(tab.ChatPanel, events);
        var lazyApproval = new Lazy<IApprovalUi>(() => approvalUi);
        var lazyPanel = new Lazy<ChatPanel>(() => tab.ChatPanel);

        IAgentTool[] tools =
        [
            new RunCommandTool(tab.Terminal, guard, lazyApproval, events),
            new AskUserTool(lazyPanel, events),
            new ReadTerminalTool(tab.ScreenBuffer, events),
            new ListDirectoryTool(events),
            new ReadFileTool(lazyApproval, events),
            new WriteFileTool(tab.Terminal, lazyApproval, events),
            new EditFileTool(tab.Terminal, lazyApproval, events),
            new SaveMemoryTool(memory, events),
            new DeleteMemoryTool(memory, events),
            new UpdateMemoryTool(memory, events),
            new ListMemoryTool(memory, events),
        ];

        var chatService = new ChatService(ChatClientFactory, guardClient, tools, memoryBlock, tab.Terminal, provider.SupportsWebSearch);
        tab.ChatService = chatService;

        tab.TaskDescription?.Dispose();
        tab.TaskDescription = new TaskDescriptionService();
        tab.TaskDescription.Start(guardClient, chatService, tab.ScreenBuffer);
        tab.Terminal.UserCommandDetected += () => tab.TaskDescription?.SignalFirstCommand();
        tab.ChatPanel.MessageSent += () => tab.TaskDescription?.SignalFirstCommand();

        events.ThinkingStarted += () => Dispatcher.BeginInvoke(() => tab.Spinner!.StartThinking());
        events.ThinkingStopped += () => Dispatcher.BeginInvoke(() =>
        {
            tab.Spinner!.Stop();
            if (tab != _activeTab && _workingWhenLeft.Contains(tab.Id))
            {
                TabBar.HighlightTab(tab.Id);
                _highlightedTabs.Add(tab.Id);
                _workingWhenLeft.Remove(tab.Id);
            }
            if (!IsActive)
                _native.FlashWindow(this);
        });
        events.CommandStarting += () => Dispatcher.BeginInvoke(() => tab.Spinner!.StartCommand());
        events.CardWaitStarted += () => Dispatcher.BeginInvoke(() =>
        {
            tab.Spinner!.Stop();
            if (tab != _activeTab && _workingWhenLeft.Contains(tab.Id))
            {
                TabBar.HighlightTab(tab.Id);
                _highlightedTabs.Add(tab.Id);
                _workingWhenLeft.Remove(tab.Id);
            }
            if (!IsActive)
                _native.FlashWindow(this);
        });
        events.CardWaitEnded += () => Dispatcher.BeginInvoke(() =>
        {
            if (tab.ChatPanel.IsStreaming) tab.Spinner!.StartThinking();
        });

        tab.Terminal.CommandCompleted += () => Dispatcher.BeginInvoke(() =>
        {
            if (tab.ChatPanel.IsStreaming)
                tab.Spinner?.StartThinking();
            else
                tab.Spinner?.Stop();
        });

        tab.TaskDescription.TaskChanged += task =>
        {
            if (!tab.IsManuallyNamed && task != null)
            {
                tab.Title = task;
                Dispatcher.BeginInvoke(() =>
                {
                    TabBar.UpdateTitle(tab.Id, task);
                    if (tab == _activeTab)
                        UpdateTitle();
                });
            }
        };
        tab.ChatPanel.UserTyping += () => tab.TaskDescription?.OnUserTyping();

        tab.ChatPanel.Initialize(chatService, events, memory: memory);
        if (tab == _activeTab)
            tab.ChatPanel.FocusPrimaryInput();
        return;

        IChatClient ChatClientFactory() => provider.CreateChatClient(apiKey);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // When maximized, Windows extends the window beyond screen edges by the resize border.
        // Compensate so content isn't clipped under the screen edge.
        OuterGrid.Margin = WindowState == WindowState.Maximized
            ? new Thickness(7)
            : new Thickness(0);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _native.InitializeWindow(new WindowInteropHelper(this).Handle, v => Topmost = v,
            _initialProvider, OnProviderSelected);

        _terminalTheme = new TerminalTheme
        {
            DefaultBackground = EasyTerminalControl.ColorToVal(Color.FromRgb(0x0C, 0x0C, 0x0C)),
            DefaultForeground = EasyTerminalControl.ColorToVal(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            DefaultSelectionBackground = EasyTerminalControl.ColorToVal(Colors.White),
            CursorStyle = CursorStyle.BlinkingBar,
            ColorTable =
            [
                0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1,
                0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
                0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9,
                0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2,
            ],
        };

        foreach (var tab in _tabs)
            tab.TerminalControl.Theme = _terminalTheme;
    }

    private async void OnProviderSelected(AiProviderKind kind)
    {
        var stored = await _settings.LoadAsync();
        if (kind == (stored.Provider ?? AiProviderKind.OpenAi)) return;

        var key = stored.KeyForProvider(kind);
        if (!string.IsNullOrEmpty(key))
        {
            await _settings.SetProviderAsync(kind);
            await ActivateAi(key);
            UpdateProviderCheckmark(kind);
        }
        else
        {
            var label = kind == AiProviderKind.OpenAi ? "OpenAI" : "Anthropic";
            _pendingProviderConstraint = kind;
            _activeTab?.ChatPanel.Initialize(null, null, $"Enter your {label} API key", OnApiKeySubmitted);
        }
    }

    private void UpdateProviderCheckmark(AiProviderKind kind)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _native.UpdateProviderCheck(hwnd, kind);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_activeTab == null) return;
        var chatPanel = _activeTab.ChatPanel;

        if (e.Key == Key.Escape)
        {
            if (chatPanel.CurrentFocus == FocusTarget.ChatLogText)
            {
                chatPanel.CurrentFocus = FocusTarget.Input;
                e.Handled = true;
                return;
            }
            var handled = false;
            if (!chatPanel.HasActiveCard && chatPanel.CancelStreaming()) handled = true;
            if (_activeTab.Terminal.IsCommandRunning) { _activeTab.Terminal.SendCtrlC(); handled = true; }
            if (handled) { e.Handled = true; return; }
        }

        // exclude AltGr (Ctrl+Alt) so Norwegian/international characters like @ aren't swallowed
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            if (e.Key == Key.Left)
            {
                var col = ContentGrid.ColumnDefinitions[0];
                col.Width = new GridLength(Math.Max(250, col.ActualWidth - ActualWidth * 0.05));
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Right)
            {
                var col = ContentGrid.ColumnDefinitions[0];
                col.Width = new GridLength(Math.Min(ActualWidth - 3 - 300, col.ActualWidth + ActualWidth * 0.05));
                e.Handled = true;
                return;
            }

            // Ctrl+T: new tab
            if (e.Key == Key.T)
            {
                _ = CreateTab();
                e.Handled = true;
                return;
            }

            // Ctrl+W: close current tab
            if (e.Key == Key.W)
            {
                CloseTab(_activeTab);
                e.Handled = true;
                return;
            }

            // Ctrl+Tab / Ctrl+Shift+Tab: next/previous tab
            if (e.Key == Key.Tab)
            {
                if (_tabs.Count > 1)
                {
                    var idx = _tabs.IndexOf(_activeTab);
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                        idx = (idx - 1 + _tabs.Count) % _tabs.Count;
                    else
                        idx = (idx + 1) % _tabs.Count;
                    SwitchToTab(_tabs[idx]);
                }
                e.Handled = true;
                return;
            }

            // Ctrl+1-9: jump to tab by index
            var digit = KeyToDigit(e.Key);
            if (digit >= 1 && digit <= 9)
            {
                if (digit <= _tabs.Count)
                    SwitchToTab(_tabs[digit - 1]);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+Space: toggle focus within active tab
        if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            if (chatPanel.HasActiveCard) return;
            if (chatPanel.CurrentFocus == FocusTarget.Console)
                chatPanel.FocusPrimaryInput();
            else
                _activeTab.TerminalControl.Focus();
        }
    }

    private static int KeyToDigit(Key key) => key switch
    {
        Key.D1 => 1,
        Key.D2 => 2,
        Key.D3 => 3,
        Key.D4 => 4,
        Key.D5 => 5,
        Key.D6 => 6,
        Key.D7 => 7,
        Key.D8 => 8,
        Key.D9 => 9,
        _ => 0
    };

    private void GridSplitter_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // CurrentFocus tracks active focus state
    }

    private void GridSplitter_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_activeTab == null) return;
            if (_activeTab.ChatPanel.CurrentFocus == FocusTarget.Console)
                _activeTab.TerminalControl.Focus();
            else
                _activeTab.ChatPanel.FocusPrimaryInput();
        });
    }
}

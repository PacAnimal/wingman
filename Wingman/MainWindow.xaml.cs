using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EasyWindowsTerminalControl;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Terminal.Wpf;
using OpenAI;

namespace Wingman;

public partial class MainWindow
{
    private static readonly SolidColorBrush FocusBorderBrush = new(Color.FromRgb(0x4A, 0x67, 0x85));

    private readonly ILoggerFactory _loggerFactory;
    private readonly IWindowsNative _native;
    private readonly ITerminal _terminal;
    private readonly ISettingsService _settings;
    private readonly IScreenBuffer _screenBuffer;
    private readonly TitleSpinner? _spinner;
    private TaskDescriptionService? _taskDescription;
    private FocusTarget _focusBeforeCard;

    public MainWindow(ILoggerFactory loggerFactory, IWindowsNative native, ITerminal terminal, ISettingsService settings, IScreenBuffer screenBuffer, string? startupError)
    {
        _loggerFactory = loggerFactory;
        _native = native;
        _terminal = terminal;
        _settings = settings;
        _screenBuffer = screenBuffer;
        InitializeComponent();

        _spinner = new TitleSpinner(UpdateTitle);
        TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();

        ChatPanel.Initialize(null, null, startupError, OnApiKeySubmitted);
        ChatPanel.ResetRequested += async () =>
        {
            await _terminal.Reset();
            _taskDescription?.Reset();
        };
        ChatPanel.CardActiveChanged += cardActive =>
        {
            if (cardActive)
            {
                _focusBeforeCard = ChatPanel.CurrentFocus;
                ChatPanel.CurrentFocus = FocusTarget.QuestionCard;
                TerminalBorder.BorderBrush = Brushes.Transparent;
            }
            else
            {
                if (_focusBeforeCard == FocusTarget.Console)
                    Terminal.Focus();
                else
                    ChatPanel.FocusPrimaryInput();
            }
        };

        if (!_native.ProbeConPTY())
            MessageBox.Show("FAILED to load conpty.dll — ConPTY will not work.",
                "Missing Native DLL", MessageBoxButton.OK, MessageBoxImage.Error);

        _native.HookPreprocessMessage(
            () => ChatPanel.CurrentFocus == FocusTarget.ChatLogText,
            () => Terminal.Terminal.GetSelectedText(),
            () => Dispatcher.BeginInvoke(Terminal.Focus));
        Closed += (_, _) => _native.UnhookPreprocessMessage();

        _terminal.ProcessExited += () => Dispatcher.BeginInvoke(Close);
        _terminal.CommandCompleted += () =>
        {
            if (ChatPanel.CurrentFocus != FocusTarget.Console)
                Dispatcher.BeginInvoke(() => Terminal.IsCursorVisible = false);
        };

        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => ChatPanel.FocusPrimaryInput();
        Closing += (_, _) => Hide();

        // cursor visibility + focus outline for terminal
        Terminal.GotFocus += (_, _) =>
        {
            ChatPanel.CurrentFocus = FocusTarget.Console;
            Terminal.IsCursorVisible = true;
            TerminalBorder.BorderBrush = FocusBorderBrush;
        };
        Terminal.LostFocus += (_, _) =>
        {
            Terminal.IsCursorVisible = false;
            TerminalBorder.BorderBrush = Brushes.Transparent;
        };

        // Init() must run synchronously here (UI thread) so DisconnectConPTYTerm() happens
        // before Show() → Loaded fires — otherwise the control races us with the default factory
        _ = InitTerminal();
    }

    private async Task InitTerminal()
    {
        await _terminal.Init(Terminal);
        // cursor hide must come after init — the ANSI escape is only received once the
        // connection is live, and Init awaits the command lock so the terminal is fully ready
        if (!Terminal.IsKeyboardFocusWithin)
            Terminal.IsCursorVisible = false;
    }

    private void UpdateTitle(char? frame)
    {
        var task = _taskDescription?.CurrentTask;
        Title = (frame, task) switch
        {
            ({ } f, { } t) => $"{f} Wingman - {t}",
            ({ } f, null) => $"{f} Wingman",
            (null, { } t) => $"Wingman - {t}",
            _ => "Wingman"
        };
        TaskbarItemInfo?.ProgressState = frame != null
                ? System.Windows.Shell.TaskbarItemProgressState.Indeterminate
                : System.Windows.Shell.TaskbarItemProgressState.None;
    }

    private async Task<string?> OnApiKeySubmitted(string key)
    {
        var error = await _settings.ValidateKeyAsync(key);
        if (error != null) return error;
        await _settings.SaveKeyAsync(key);
        ActivateAi(key);
        return null;
    }

    internal void ActivateAi(string apiKey)
    {
        var openAiClient = new OpenAIClient(apiKey);

        var guardClient = openAiClient.GetResponsesClient(Constants.GuardModel).AsIChatClient();
        var guard = new CommandGuard(guardClient, _loggerFactory.CreateLogger<CommandGuard>());

        var events = new AgentEvents();
        var approvalUi = new ApprovalUI(ChatPanel);
        var lazyApproval = new Lazy<IApprovalUI>(() => approvalUi);
        var lazyPanel = new Lazy<ChatPanel>(() => ChatPanel);

        IAgentTool[] tools =
        [
            new RunCommandTool(_terminal, guard, lazyApproval, events),
            new AskUserTool(lazyPanel, events),
            new ReadTerminalTool(_screenBuffer, events),
            new ListDirectoryTool(events),
            new ReadFileTool(lazyApproval, events),
            new WriteFileTool(_terminal, lazyApproval, events),
        ];

        var chatService = new ChatService(ChatClientFactory, tools);

        _taskDescription = new TaskDescriptionService();
        _taskDescription.Start(guardClient, chatService, _screenBuffer);

        events.ThinkingStarted += () => Dispatcher.BeginInvoke(() => _spinner!.StartThinking());
        events.ThinkingStopped += () => Dispatcher.BeginInvoke(() => _spinner!.Stop());
        events.CommandStarting += () => Dispatcher.BeginInvoke(() => _spinner!.StartCommand());

        _terminal.CommandCompleted += () => Dispatcher.BeginInvoke(() =>
        {
            if (ChatPanel.IsStreaming)
                _spinner?.StartThinking();
            else
                _spinner?.Stop();
            _taskDescription?.SignalFirstCommand();
        });

        _taskDescription.TaskChanged += _ => Dispatcher.BeginInvoke(() => UpdateTitle(_spinner?.CurrentFrame));
        ChatPanel.UserTyping += () => _taskDescription.OnUserTyping();

        ChatPanel.Initialize(chatService, events);
        ChatPanel.FocusPrimaryInput();
        return;

        IChatClient ChatClientFactory() => openAiClient.GetResponsesClient(Constants.ChatModel).AsIChatClient().AsBuilder().UseFunctionInvocation().Build();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _native.InitializeWindow(new WindowInteropHelper(this).Handle, v => Topmost = v);

        // set terminal theme (campbell defaults, smaller font)
        Terminal.Theme = new TerminalTheme
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
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ChatPanel.CurrentFocus == FocusTarget.ChatLogText)
            {
                ChatPanel.CurrentFocus = FocusTarget.Input; // dismiss selection
                e.Handled = true;
                return;
            }
            if (!ChatPanel.HasActiveCard && ChatPanel.CancelStreaming()) { e.Handled = true; return; }
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.Left)
            {
                // shrink chat panel by 5%
                var col = MainGrid.ColumnDefinitions[0];
                col.Width = new GridLength(Math.Max(250, col.ActualWidth - ActualWidth * 0.05));
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Right)
            {
                // grow chat panel by 5%, keeping at least 300px for the terminal
                var col = MainGrid.ColumnDefinitions[0];
                col.Width = new GridLength(Math.Min(ActualWidth - 3 - 300, col.ActualWidth + ActualWidth * 0.05));
                e.Handled = true;
                return;
            }
        }

        if (e.Key != Key.Space || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;

        // block toggle while a card needs user input
        if (ChatPanel.HasActiveCard) return;

        // toggle focus between chat input and terminal
        if (ChatPanel.CurrentFocus == FocusTarget.Console)
            ChatPanel.FocusPrimaryInput();
        else
            Terminal.Focus();
    }

    private void GridSplitter_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // CurrentFocus tracks active focus state
    }

    private void GridSplitter_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        // restore focus after splitter drag completes
        Dispatcher.BeginInvoke(() =>
        {
            if (ChatPanel.CurrentFocus == FocusTarget.Console)
                Terminal.Focus();
            else
                ChatPanel.FocusPrimaryInput();
        });
    }

}

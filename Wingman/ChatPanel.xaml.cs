using System.Diagnostics;
using System.Windows;
using Cathedral.Utils;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wingman;

public enum FocusTarget { Input, Console, QuestionCard, ChatLogText }

public partial class ChatPanel
{
    private static readonly string[] HintStrings =
    [
        "Hit Ctrl+Space switches focus",
        "Hit Press Esc to cancel the AI",
        "Hit Ctrl+Enter for a new line",
        "Type /reset to start fresh",
        "Hit Shift+Enter accepts approval cards",
        "Ctrl+Arrow keys resize the panels",
    ];

    private static readonly string[] SlashCommandNames = ["/reset"];

    private static readonly ControlTemplate BubbleTextTemplate = MakeBubbleTextTemplate();
    private static readonly ControlTemplate CopyBtnTemplate = MakeCopyBtnTemplate();

    private IChatService? _chatService;
    private AgentEvents? _agentEvents;
    private Func<string, Task<string?>>? _onApiKeySubmitted;
    private bool _isStreaming;
    private CancellationTokenSource? _streamingCts;
    private readonly Toggle _needNewBubble = new();
    private TypingIndicator? _typing;
    private TextBox? _currentBubble;
    private bool _currentBubbleHasContent;
    private TaskCompletionSource<bool>? _pendingApproval;
    private Action<bool>? _pendingApprovalCallback;
    private TaskCompletionSource<string?>? _pendingChoice;
    private Action<string?>? _pendingChoiceCallback;
    private string[]? _pendingChoiceOptions;
    private Border? _activeCard;
    private Brush? _savedCaretBrush;
    private double _bubbleMaxWidth = 360;
    private bool _suppressCompletion;

    private FocusTarget _currentFocus = FocusTarget.Input;
    private TextBox? _selectedBubble;
    private TextBox? _mouseDownBubble;

    public FocusTarget CurrentFocus
    {
        get => _currentFocus;
        set
        {
            var prev = _currentFocus;
            _currentFocus = value; // update first to prevent re-entrancy via InputBox.GotFocus
            if (prev == FocusTarget.ChatLogText && value != FocusTarget.ChatLogText)
            {
                _selectedBubble?.Select(0, 0);
                _selectedBubble = null;
                InputBox.Focus(); // bubble held keyboard focus; return it to InputBox
            }
        }
    }

    private readonly string[] _hints;
    private int _hintIndex;
    private readonly DispatcherTimer _hintTimer;
    private readonly Stopwatch _hintWatch = new();

    public ChatPanel()
    {
        InitializeComponent();

        InputBox.TextChanged += (_, _) => { UpdateCompletionPopup(); UserTyping?.Invoke(); };
        InputBox.GotFocus += (_, _) => CurrentFocus = FocusTarget.Input;

        // abort pending card if focus leaves the panel entirely (e.g. user switches to terminal)
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue) return;
            if (_pendingChoice != null) ResolveChoice(null);
            if (_pendingApproval != null) ResolveApproval(false);
        };

        // dynamic bubble width: track 85% of the scroll viewer's actual width
        MessagesScrollViewer.SizeChanged += (_, _) =>
        {
            _bubbleMaxWidth = Math.Max(200, MessagesScrollViewer.ActualWidth * 0.85);
            foreach (UIElement child in MessagesPanel.Children)
                if (child is Grid g && "bubble".Equals(g.Tag))
                    g.MaxWidth = _bubbleMaxWidth;
        };

        // track which bubble was clicked — defer all focus decisions to mouse up
        // so drag selection isn't interrupted by an early InputBox.Focus()
        MessagesScrollViewer.PreviewMouseDown += (_, e) =>
        {
            if (_activeCard != null) return;
            _mouseDownBubble = e.OriginalSource is DependencyObject src ? FindBubbleTextBox(src) : null;
        };

        // after mouse-up: if text was selected in a bubble, track it and leave focus on the bubble;
        // otherwise focus InputBox
        MessagesScrollViewer.PreviewMouseUp += (_, _) =>
        {
            var bubble = _mouseDownBubble;
            _mouseDownBubble = null;
            if (bubble != null && !string.IsNullOrEmpty(bubble.SelectedText))
            {
                if (_selectedBubble != bubble)
                    _selectedBubble?.Select(0, 0);
                _selectedBubble = bubble;
                CurrentFocus = FocusTarget.ChatLogText; // bubble keeps keyboard focus
            }
            else
            {
                InputBox.Focus();
            }
        };

        // shuffled hints — different order each launch
        _hints = [.. HintStrings];
        for (var i = _hints.Length - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (_hints[i], _hints[j]) = (_hints[j], _hints[i]);
        }
        HintText.Text = "Hint: " + _hints[0];

        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _hintTimer.Tick += OnHintTimerTick;
    }

    private void OnHintTimerTick(object? sender, EventArgs e)
    {
        _hintIndex = (_hintIndex + 1) % _hints.Length;
        HintText.Text = "Hint: " + _hints[_hintIndex];
        _hintWatch.Restart();
        _hintTimer.Interval = TimeSpan.FromSeconds(15);
    }

    public void Initialize(IChatService? chatService, AgentEvents? agentEvents, string? errorMessage = null, Func<string, Task<string?>>? onApiKeySubmitted = null)
    {
        _chatService = chatService;
        _onApiKeySubmitted = onApiKeySubmitted;

        // detach old handler before attaching new one
        if (_agentEvents != null)
            _agentEvents.ToolStarted -= OnToolStarted;
        _agentEvents = agentEvents;

        // each tool execution signals a bubble break; the flag is read on the UI thread
        // between chunks, so volatile is enough — no dispatcher needed
        if (_agentEvents != null)
            _agentEvents.ToolStarted += OnToolStarted;

        if (chatService == null)
        {
            OverlayMessage.Text = errorMessage ?? string.Empty;
            DisabledOverlay.Visibility = Visibility.Visible;
            ApiKeyBox.Focus();
        }
        else
        {
            DisabledOverlay.Visibility = Visibility.Collapsed;
            _onApiKeySubmitted = null;
        }
    }

    private void OnToolStarted() => _needNewBubble.TrySet();

    public bool IsStreaming => _isStreaming;

    public event Action? UserTyping;
    public event Action<bool>? CardActiveChanged;
    public bool HasActiveCard => _activeCard != null;

    public event Func<Task>? ResetRequested;

    private void ActivatePending(Border card)
    {
        _activeCard = card;
        card.BorderThickness = new Thickness(1);
        card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
        card.Focusable = true;
        card.FocusVisualStyle = null;
        _savedCaretBrush = InputBox.CaretBrush;
        InputBox.CaretBrush = Brushes.Transparent;
        CardActiveChanged?.Invoke(true);
        // stop typing; remove the empty bubble so the card lands at the bottom
        _typing?.Stop();
        if (!_currentBubbleHasContent && _currentBubble?.Parent is Border { Parent: Grid wrapperGrid })
            MessagesPanel.Children.Remove(wrapperGrid);
        // focus is moved to card in InsertElement, after it's in the visual tree
    }

    private void DeactivatePending()
    {
        _activeCard = null;
        InputBox.CaretBrush = _savedCaretBrush;
        _savedCaretBrush = null;
        CardActiveChanged?.Invoke(false);
        // if streaming, open a fresh bubble below the card summary and resume typing
        if (_isStreaming)
        {
            _currentBubble = AddBubble("", isUser: false);
            _currentBubbleHasContent = false;
            _typing?.Retarget(_currentBubble);
            _typing?.Start();
            _needNewBubble.TryReset();
        }
    }

    public void SetPendingApproval(TaskCompletionSource<bool> tcs, Action<bool> onResolved, Border card)
    {
        _pendingApproval = tcs;
        _pendingApprovalCallback = onResolved;
        ActivatePending(card);
    }

    private void ResolveApproval(bool accept)
    {
        var tcs = _pendingApproval;
        var cb = _pendingApprovalCallback;
        _pendingApproval = null;
        _pendingApprovalCallback = null;
        cb?.Invoke(accept);  // summary inserted before new bubble
        DeactivatePending();
        tcs?.TrySetResult(accept);
    }

    public void SetPendingChoice(TaskCompletionSource<string?> tcs, Action<string?> onResolved, string[] options, Border card)
    {
        _pendingChoice = tcs;
        _pendingChoiceCallback = onResolved;
        _pendingChoiceOptions = options;
        ActivatePending(card);
    }

    private void ResolveChoice(string? selected)
    {
        var tcs = _pendingChoice;
        var cb = _pendingChoiceCallback;
        _pendingChoice = null;
        _pendingChoiceCallback = null;
        _pendingChoiceOptions = null;
        cb?.Invoke(selected);  // summary inserted before new bubble
        DeactivatePending();
        tcs?.TrySetResult(selected);
    }

    private static int KeyToDigit(Key key) => key switch
    {
        Key.D1 or Key.NumPad1 => 1,
        Key.D2 or Key.NumPad2 => 2,
        Key.D3 or Key.NumPad3 => 3,
        Key.D4 or Key.NumPad4 => 4,
        Key.D5 or Key.NumPad5 => 5,
        Key.D6 or Key.NumPad6 => 6,
        Key.D7 or Key.NumPad7 => 7,
        Key.D8 or Key.NumPad8 => 8,
        Key.D9 or Key.NumPad9 => 9,
        _ => 0
    };

    public bool CancelStreaming()
    {
        if (!_isStreaming || _streamingCts == null) return false;
        _streamingCts.Cancel();
        return true;
    }

    private async Task<bool> TryExecuteSlashCommand(string text)
    {
        if (!text.Equals("/reset", StringComparison.OrdinalIgnoreCase)) return false;
        CancelStreaming();
        _chatService?.ClearHistory();
        MessagesPanel.Children.Clear();
        if (ResetRequested != null)
            await ResetRequested.Invoke();
        return true;
    }

    private void UpdateCompletionPopup()
    {
        if (_suppressCompletion) return;
        var text = InputBox.Text;
        if (text.StartsWith('/'))
        {
            var matches = SlashCommandNames
                .Where(c => c.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length > 0)
            {
                CompletionList.ItemsSource = matches;
                CompletionList.SelectedIndex = 0;
                CompletionPopup.IsOpen = true;
                return;
            }
        }
        CompletionPopup.IsOpen = false;
    }

    private void AcceptCompletion()
    {
        if (CompletionList.SelectedItem is not string command) return;
        _suppressCompletion = true;
        InputBox.Text = command;
        InputBox.CaretIndex = command.Length;
        CompletionPopup.IsOpen = false;
        _suppressCompletion = false;
    }

    public TextBox InputTextBox => InputBox;

    public void FocusPrimaryInput()
    {
        if (DisabledOverlay.Visibility == Visibility.Visible)
            ApiKeyBox.Focus();
        else
            InputBox.Focus();
    }

    // panel-level handler: intercepts keys regardless of which child has focus
    private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CurrentFocus == FocusTarget.ChatLogText && _selectedBubble != null && !IsModifierKey(e.Key))
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Clipboard.SetText(_selectedBubble.SelectedText);
                CurrentFocus = FocusTarget.Input; // setter clears selection + returns focus to InputBox
                e.Handled = true;
                return;
            }
            // any other key: dismiss selection (setter returns focus to InputBox);
            // leave e.Handled false so the subsequent TextInput event routes to InputBox
            CurrentFocus = FocusTarget.Input;
            return;
        }

        if (_pendingChoice != null && !IsModifierKey(e.Key))
        {
            // shift+enter / ctrl+enter: ignore — user may confuse with approval card
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.None)
            {
                e.Handled = true;
                return;
            }

            var digit = KeyToDigit(e.Key);
            if (digit >= 1 && digit <= _pendingChoiceOptions!.Length)
            {
                ResolveChoice(_pendingChoiceOptions[digit - 1]);
            }
            else
                ResolveChoice(null);
            e.Handled = true;
            return;
        }

        if (_pendingApproval != null && !IsModifierKey(e.Key))
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                ResolveApproval(true);
            else
                ResolveApproval(false);
            e.Handled = true;
        }
    }

    private async void ApiKeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _onApiKeySubmitted == null) return;
        e.Handled = true;

        var key = ApiKeyBox.Text;
        if (string.IsNullOrWhiteSpace(key)) return;

        ApiKeyBox.IsEnabled = false;
        OverlayStatus.Text = "Validating...";
        OverlayMessage.Text = string.Empty;

        var error = await _onApiKeySubmitted(key);
        if (error != null)
        {
            OverlayMessage.Text = error;
            OverlayStatus.Text = "Press Enter to submit";
            ApiKeyBox.IsEnabled = true;
            ApiKeyBox.Focus();
        }
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CompletionPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Tab:
                    AcceptCompletion();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    CompletionPopup.IsOpen = false;
                    e.Handled = true;
                    return;
                case Key.Up:
                    CompletionList.SelectedIndex = Math.Max(0, CompletionList.SelectedIndex - 1);
                    e.Handled = true;
                    return;
                case Key.Down:
                    CompletionList.SelectedIndex = Math.Min(CompletionList.Items.Count - 1, CompletionList.SelectedIndex + 1);
                    e.Handled = true;
                    return;
                case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                    AcceptCompletion();
                    e.Handled = true;
                    _ = SendMessage();
                    return;
            }
        }

        if (e.Key != Key.Enter) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // ctrl+enter: insert newline
            var idx = InputBox.CaretIndex;
            InputBox.Text = InputBox.Text.Insert(idx, "\r\n");
            InputBox.CaretIndex = idx + 2;
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

        // plain enter: send
        e.Handled = true;
        _ = SendMessage();
    }

    private TextBox? FindBubbleTextBox(DependencyObject obj)
    {
        while (obj != null)
        {
            if (obj is TextBox tb && tb != InputBox) return tb;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftShift or Key.RightShift or
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.System or Key.LWin or Key.RWin or Key.CapsLock;

    private async Task SendMessage()
    {
        var userText = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(userText)) return;

        // slash commands work even during streaming or without an API key
        if (userText.StartsWith('/'))
        {
            InputBox.Text = "";
            if (await TryExecuteSlashCommand(userText)) return;
            // unknown slash command — restore text so user can see/edit it
            InputBox.Text = userText;
            InputBox.CaretIndex = userText.Length;
            return;
        }

        if (_isStreaming || _chatService == null) return;

        InputBox.Text = "";
        _isStreaming = true;
        _agentEvents?.RaiseThinkingStarted();

        // start hint rotation while streaming (respects elapsed time in current 15s window)
        var remaining = TimeSpan.FromSeconds(15) - _hintWatch.Elapsed;
        _hintTimer.Interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(15);
        _hintTimer.Start();
        _hintWatch.Start();

        AddBubble(userText, isUser: true);
        _currentBubble = AddBubble("", isUser: false);
        _currentBubbleHasContent = false;
        _typing = new TypingIndicator(_currentBubble);
        _typing.Start();

        _streamingCts = new CancellationTokenSource();
        try
        {
            await foreach (var chunk in _chatService.SendMessageAsync(userText, _streamingCts.Token))
            {
                // accepted tool ran — open a fresh bubble for the post-tool response
                if (_needNewBubble)
                {
                    _needNewBubble.TryReset();
                    if (_currentBubbleHasContent)
                    {
                        _currentBubble = AddBubble("", isUser: false);
                        _currentBubbleHasContent = false;
                        _typing.Retarget(_currentBubble);
                    }
                }

                if (!_currentBubbleHasContent)
                {
                    _typing.Stop();
                    _currentBubble!.Text = "";
                    _currentBubbleHasContent = true;
                }
                _currentBubble!.Text += chunk;
                ScrollToBottom();
            }
        }
        catch (OperationCanceledException)
        {
            _typing?.Stop();
            InsertElement(new TextBlock
            {
                Text = "Interrupted!",
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize = 11,
                Margin = new Thickness(10, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left
            });
        }
        catch (Exception ex)
        {
            _typing?.Stop();
            _currentBubble!.Text = $"[Error: {ex.Message}]";
            _currentBubble.Foreground = Brushes.IndianRed;
        }
        finally
        {
            _hintTimer.Stop();
            _hintWatch.Stop();
            _typing?.Dispose();
            _typing = null;
            _streamingCts?.Dispose();
            _streamingCts = null;
            _agentEvents?.RaiseThinkingStopped();
            _isStreaming = false;
        }
    }

    private TextBox AddBubble(string text, bool isUser)
    {
        var textBox = new TextBox
        {
            IsReadOnly = true,
            IsTabStop = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            UndoLimit = 0,
            IsUndoEnabled = false,
            CaretBrush = Brushes.Transparent,
            FocusVisualStyle = null,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
            FontSize = 13,
            Text = text,
            Template = BubbleTextTemplate,
            IsInactiveSelectionHighlightEnabled = true,
        };
        textBox.Resources[SystemColors.InactiveSelectionHighlightBrushKey] =
            new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78));
        var border = new Border
        {
            Child = textBox,
            Background = new SolidColorBrush(isUser
                ? Color.FromRgb(0x0E, 0x63, 0x9C)
                : Color.FromRgb(0x2D, 0x2D, 0x2D)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
        };
        var copyBtn = new Button
        {
            Content = "\uE8C8",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 2, 0),
            Padding = new Thickness(4, 2, 4, 2),
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Color.FromArgb(0xBB, 0x1E, 0x1E, 0x1E)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Template = CopyBtnTemplate,
        };
        var wrapper = new Grid
        {
            Tag = "bubble",
            MaxWidth = _bubbleMaxWidth,
            Margin = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        wrapper.Children.Add(border);
        wrapper.Children.Add(copyBtn);
        wrapper.MouseEnter += (_, _) => copyBtn.Visibility = Visibility.Visible;
        wrapper.MouseLeave += (_, _) => copyBtn.Visibility = Visibility.Collapsed;
        copyBtn.Click += (_, _) => Clipboard.SetText(textBox.Text);
        MessagesPanel.Children.Add(wrapper);
        ScrollToBottom();
        return textBox;
    }

    public void InsertElement(UIElement element)
    {
        MessagesPanel.Children.Add(element);
        ScrollToBottom();
        // focus the card after it's in the visual tree so the TextBox border clears naturally
        if (element == _activeCard)
            _activeCard.Focus();
    }

    public void RemoveElement(UIElement element) => MessagesPanel.Children.Remove(element);

    private void ScrollToBottom() => MessagesScrollViewer.ScrollToEnd();

    private static ControlTemplate MakeBubbleTextTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer))
        {
            Name = "PART_ContentHost"
        };
        sv.SetValue(ScrollViewer.FocusableProperty, false);
        sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        template.VisualTree = sv;
        return template;
    }

    private static ControlTemplate MakeCopyBtnTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        template.VisualTree = bd;
        return template;
    }

    private sealed class TypingIndicator : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private int _dotCount;
        private TextBox _target;

        public TypingIndicator(TextBox target)
        {
            _target = target;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) =>
            {
                _dotCount = (_dotCount + 1) % 4;
                _target.Text = _dotCount > 0 ? new string('.', _dotCount) : "";
            };
        }

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();

        public void Retarget(TextBox newTarget)
        {
            _timer.Stop();
            _dotCount = 0;
            _target = newTarget;
            _timer.Start();
        }

        public void Dispose() => _timer.Stop();
    }
}

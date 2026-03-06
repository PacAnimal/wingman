using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using Cathedral.Utils;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text;

namespace Wingman;

public enum FocusTarget { Input, Console, QuestionCard, ChatLogText }

public partial class ChatPanel
{
    private static readonly string[] HintStrings =
    [
        "Ctrl+Space switches focus between chat and terminal",
        "Ctrl+T opens a new tab, Ctrl+W closes the current tab",
        "Ctrl+Tab / Ctrl+Shift+Tab switches between tabs",
        "Press Esc to cancel the AI",
        "Ctrl+Enter for a new line",
        "Type /reset to start fresh",
        "Shift+Enter accepts approval cards",
        "Ctrl+Arrow keys resize the panels",
    ];

    private static readonly string[] SlashCommandNames = ["/reset", "/memory", "/forget"];
    private static readonly string[] ForgetCompletions =
        ["/forget everything", .. Enumerable.Range(1, 9).Select(i => $"/forget {i}")];

    private static readonly ControlTemplate BubbleTextTemplate = MakeBubbleTextTemplate();
    private static readonly ControlTemplate RichBubbleTemplate = MakeRichBubbleTemplate();
    private static readonly ControlTemplate CopyBtnTemplate = MakeCopyBtnTemplate();

    private IChatService? _chatService;
    private AgentEvents? _agentEvents;
    private IMemoryService? _memory;
    private readonly ConcurrentQueue<string> _pendingActivities = new();
    private Func<string, Task<string?>>? _onApiKeySubmitted;
    private bool _isStreaming;
    private CancellationTokenSource? _streamingCts;
    private readonly Toggle _needNewBubble = new();
    private TypingIndicator? _typing;
    private TextBox? _currentBubble;           // typing placeholder for the current AI bubble
    private RichTextBox? _currentRichBubble;   // actual AI content bubble
    private StringBuilder? _mdAccumulator;     // raw markdown accumulated during streaming
    private string _lastRenderedMd = "";       // dedup guard to skip redundant renders
    private Border? _currentAiBubbleBorder;    // border wrapping the current AI bubble
    private bool _currentBubbleHasContent;
    private TaskCompletionSource<bool>? _pendingApproval;
    private Action<bool>? _pendingApprovalCallback;
    private TaskCompletionSource<string?>? _pendingChoice;
    private Action<string?>? _pendingChoiceCallback;
    private string[]? _pendingChoiceOptions;
    private Border? _activeCard;
    private Brush? _savedCaretBrush;
    private double _bubbleMaxWidth = 360;
    public double BubbleMaxWidth => _bubbleMaxWidth;
    private bool _suppressCompletion;

    private FocusTarget _currentFocus = FocusTarget.Input;
    private Control? _selectedBubble;
    private Control? _mouseDownBubble;

    public FocusTarget CurrentFocus
    {
        get => _currentFocus;
        set
        {
            var prev = _currentFocus;
            _currentFocus = value; // update first to prevent re-entrancy via InputBox.GotFocus
            if (prev == FocusTarget.ChatLogText && value != FocusTarget.ChatLogText)
            {
                ClearSelection(_selectedBubble);
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
            if (_activeCard != null) return; // card survives focus loss; re-focused on mouse up
        };

        // dynamic bubble width: track 85% of the scroll viewer's actual width
        MessagesScrollViewer.SizeChanged += (_, _) =>
        {
            _bubbleMaxWidth = Math.Max(200, MessagesScrollViewer.ActualWidth * 0.85);
            foreach (UIElement child in MessagesPanel.Children)
                if (child is FrameworkElement fe && "bubble".Equals(fe.Tag))
                    fe.MaxWidth = _bubbleMaxWidth;
        };

        // track which bubble was clicked — defer all focus decisions to mouse up
        // so drag selection isn't interrupted by an early InputBox.Focus()
        MessagesScrollViewer.PreviewMouseDown += (_, e) =>
        {
            if (_activeCard != null) return;
            _mouseDownBubble = e.OriginalSource is DependencyObject src ? FindBubbleControl(src) : null;
        };

        // after mouse-up: if text was selected in a bubble, track it and leave focus on the bubble;
        // otherwise focus InputBox
        MessagesScrollViewer.PreviewMouseUp += (_, _) =>
        {
            var bubble = _mouseDownBubble;
            _mouseDownBubble = null;
            if (bubble != null && !string.IsNullOrEmpty(GetSelectedText(bubble)))
            {
                if (_selectedBubble != bubble)
                    ClearSelection(_selectedBubble);
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

    public void Initialize(IChatService? chatService, AgentEvents? agentEvents, string? errorMessage = null, Func<string, Task<string?>>? onApiKeySubmitted = null, IMemoryService? memory = null)
    {
        _chatService = chatService;
        _memory = memory;
        _onApiKeySubmitted = onApiKeySubmitted;

        // detach old handlers before attaching new ones
        if (_agentEvents != null)
        {
            _agentEvents.ToolStarted -= OnToolStarted;
            _agentEvents.ToolActivity -= OnToolActivity;
        }
        _agentEvents = agentEvents;

        // each tool execution signals a bubble break; the flag is read on the UI thread
        // between chunks, so volatile is enough — no dispatcher needed
        if (_agentEvents != null)
        {
            _agentEvents.ToolStarted += OnToolStarted;
            _agentEvents.ToolActivity += OnToolActivity;
        }

        if (chatService == null)
        {
            OverlayMessage.Text = errorMessage ?? string.Empty;
            ApiKeyBox.Text = string.Empty;
            ApiKeyBox.IsEnabled = true;
            OverlayStatus.Text = "Press Enter to submit";
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
    private void OnToolActivity(string message) => _pendingActivities.Enqueue(message);

    public bool IsStreaming => _isStreaming;

    public event Action? UserTyping;
    public event Action<bool>? CardActiveChanged;
    public bool HasActiveCard => _activeCard != null;
    public void FocusActiveCard() => _activeCard?.Focus();

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
            _currentRichBubble = AddAiBubble();
            _mdAccumulator = new StringBuilder();
            _lastRenderedMd = "";
            _currentBubbleHasContent = false;
            _typing?.Retarget(_currentBubble!);
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
        if (text.Equals("/reset", StringComparison.OrdinalIgnoreCase))
        {
            CancelStreaming();
            _chatService?.ClearHistory();
            MessagesPanel.Children.Clear();
            if (ResetRequested != null)
                await ResetRequested.Invoke();
            return true;
        }

        if (text.Equals("/memory", StringComparison.OrdinalIgnoreCase))
        {
            if (_memory == null) return false;
            var list = await _memory.ListMemoriesAsync();
            InsertElement(MakeStatusText(list));
            return true;
        }

        if (text.StartsWith("/forget", StringComparison.OrdinalIgnoreCase))
        {
            if (_memory == null) return false;
            var arg = text["/forget".Length..].Trim();
            string result;
            if (arg.Equals("everything", StringComparison.OrdinalIgnoreCase))
                result = await _memory.DeleteAllAsync();
            else if (int.TryParse(arg, out var n))
                result = await _memory.DeleteMemoryAsync(n);
            else
                return false;
            InsertElement(MakeStatusText(result));
            return true;
        }

        return false;
    }

    private static TextBlock MakeStatusText(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
        FontSize = 11,
        Margin = new Thickness(10, 2, 8, 2),
        HorizontalAlignment = HorizontalAlignment.Left,
        TextWrapping = TextWrapping.Wrap,
    };

    private void UpdateCompletionPopup()
    {
        if (_suppressCompletion) return;
        var text = InputBox.Text;
        if (text.StartsWith('/'))
        {
            var pool = text.StartsWith("/forget ", StringComparison.OrdinalIgnoreCase)
                ? ForgetCompletions
                : SlashCommandNames;
            var matches = pool
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
                Clipboard.SetText(GetSelectedText(_selectedBubble));
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

    private Control? FindBubbleControl(DependencyObject? obj)
    {
        while (obj != null)
        {
            if (obj is RichTextBox rtb) return rtb;
            if (obj is TextBox tb && tb != InputBox) return tb;
            // ContentElements (Run, Bold, etc.) are not Visuals — use logical tree
            obj = obj is ContentElement ce
                ? ContentOperations.GetParent(ce) ?? LogicalTreeHelper.GetParent(ce)
                : VisualTreeHelper.GetParent(obj);
        }
        return null;
    }

    private static string GetSelectedText(Control? c) => c switch
    {
        TextBox tb => tb.SelectedText,
        RichTextBox rtb => rtb.Selection.Text,
        _ => "",
    };

    private static void ClearSelection(Control? c)
    {
        if (c is TextBox tb) tb.Select(0, 0);
        else if (c is RichTextBox rtb) rtb.Selection.Select(rtb.Document.ContentStart, rtb.Document.ContentStart);
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

        AddUserBubble(userText);
        _currentRichBubble = AddAiBubble();
        _mdAccumulator = new StringBuilder();
        _lastRenderedMd = "";
        _currentBubbleHasContent = false;
        _typing = new TypingIndicator(_currentBubble!);
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
                        _currentRichBubble = AddAiBubble();
                        _mdAccumulator = new StringBuilder();
                        _lastRenderedMd = "";
                        _currentBubbleHasContent = false;
                        _typing.Retarget(_currentBubble!);
                    }
                }

                // drain activity breadcrumbs above the current bubble
                var insertIdx = _currentAiBubbleBorder?.Parent is UIElement bw ? MessagesPanel.Children.IndexOf(bw) : -1;
                while (_pendingActivities.TryDequeue(out var activity))
                {
                    var tb = new TextBlock
                    {
                        Text = activity,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                        FontSize = 11,
                        Margin = new Thickness(10, 2, 8, 2),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        TextWrapping = TextWrapping.Wrap
                    };
                    if (insertIdx >= 0)
                    {
                        MessagesPanel.Children.Insert(insertIdx, tb);
                        insertIdx++;
                    }
                    else
                    {
                        InsertElement(tb);
                    }
                }
                if (insertIdx > 0) ScrollToBottom();

                if (!_currentBubbleHasContent)
                {
                    _typing.Stop();
                    // swap typing placeholder out; rich bubble takes its place
                    _currentAiBubbleBorder!.Child = _currentRichBubble;
                    _currentBubbleHasContent = true;
                }
                _mdAccumulator!.Append(chunk);
                RenderMarkdown();
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
            if (!_currentBubbleHasContent && _currentAiBubbleBorder != null)
            {
                _currentAiBubbleBorder.Child = _currentRichBubble;
                _currentBubbleHasContent = true;
            }
            var errorDoc = new FlowDocument();
            errorDoc.Blocks.Add(new Paragraph(new Run($"[Error: {ex.Message}]")) { Foreground = Brushes.IndianRed });
            _currentRichBubble?.Document = errorDoc;
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

    private void RenderMarkdown()
    {
        var md = _mdAccumulator!.ToString();
        if (md == _lastRenderedMd) return;
        _lastRenderedMd = md;
        _currentRichBubble!.Tag = md;
        _currentRichBubble.Document = MarkdownRenderer.Render(md);
    }

    private void AddUserBubble(string text)
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
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x63, 0x9C)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
        };
        var copyBtn = MakeCopyButton();
        var wrapper = new Grid
        {
            Tag = "bubble",
            MaxWidth = _bubbleMaxWidth,
            Margin = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        wrapper.Children.Add(border);
        wrapper.Children.Add(copyBtn);
        wrapper.MouseEnter += (_, _) => copyBtn.Visibility = Visibility.Visible;
        wrapper.MouseLeave += (_, _) => copyBtn.Visibility = Visibility.Collapsed;
        copyBtn.Click += (_, _) => Clipboard.SetText(textBox.Text);
        MessagesPanel.Children.Add(wrapper);
        ScrollToBottom();
    }

    private RichTextBox AddAiBubble()
    {
        // typing placeholder: hosts the TypingIndicator dots until first content arrives
        var typingBox = new TextBox
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
            Template = BubbleTextTemplate,
        };

        var rtb = new RichTextBox
        {
            IsReadOnly = true,
            IsTabStop = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            IsUndoEnabled = false,
            CaretBrush = Brushes.Transparent,
            FocusVisualStyle = null,
            Foreground = Brushes.White,
            FontSize = 13,
            Template = RichBubbleTemplate,
            IsInactiveSelectionHighlightEnabled = true,
        };
        rtb.Resources[SystemColors.InactiveSelectionHighlightBrushKey] =
            new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78));

        var border = new Border
        {
            Child = typingBox, // typing dots start here; swapped to rtb on first content
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
        };
        var copyBtn = MakeCopyButton();
        var wrapper = new Grid
        {
            Tag = "bubble",
            MaxWidth = _bubbleMaxWidth,
            Margin = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        wrapper.Children.Add(border);
        wrapper.Children.Add(copyBtn);
        wrapper.MouseEnter += (_, _) => copyBtn.Visibility = Visibility.Visible;
        wrapper.MouseLeave += (_, _) => copyBtn.Visibility = Visibility.Collapsed;
        // copy the raw markdown stored in Tag; fall back to plain text extraction
        copyBtn.Click += (_, _) => Clipboard.SetText(rtb.Tag as string
            ?? new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text.TrimEnd());
        MessagesPanel.Children.Add(wrapper);
        ScrollToBottom();

        _currentBubble = typingBox;
        _currentAiBubbleBorder = border;
        return rtb;
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

    private static Button MakeCopyButton() => new()
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

    private static ControlTemplate MakeRichBubbleTemplate()
    {
        var template = new ControlTemplate(typeof(RichTextBox));
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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wingman;

public partial class ChatPanel : UserControl
{
    private IChatService? _chatService;
    private bool _isStreaming;
    private volatile bool _needNewBubble;
    private TypingIndicator? _typing;
    private TextBlock? _currentBubble;
    private bool _currentBubbleHasContent;
    private TaskCompletionSource<bool>? _pendingApproval;
    private Action<bool>? _pendingApprovalCallback;
    private TaskCompletionSource<string?>? _pendingChoice;
    private Action<string?>? _pendingChoiceCallback;
    private string[]? _pendingChoiceOptions;
    private Border? _activeCard;
    private Brush? _savedCaretBrush;

    public ChatPanel()
    {
        InitializeComponent();
        // abort pending card if focus leaves the panel entirely (e.g. user switches to terminal)
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue) return;
            if (_pendingChoice != null) ResolveChoice(null);
            if (_pendingApproval != null) ResolveApproval(false);
        };
    }

    public void Initialize(IChatService? chatService, AgentEvents? agentEvents)
    {
        _chatService = chatService;
        if (chatService == null)
            DisabledOverlay.Visibility = Visibility.Visible;

        // each tool execution signals a bubble break; the flag is read on the UI thread
        // between chunks, so volatile is enough — no dispatcher needed
        if (agentEvents != null)
            agentEvents.ToolStarted += () => _needNewBubble = true;
    }

    public event Action<bool>? CardActiveChanged;
    public bool HasActiveCard => _activeCard != null;

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
        if (!_currentBubbleHasContent && _currentBubble?.Parent is UIElement parent)
            MessagesPanel.Children.Remove(parent);
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
            _needNewBubble = false;
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

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _chatService?.ClearHistory();
        MessagesPanel.Children.Clear();
    }

    public TextBox InputTextBox => InputBox;

    // panel-level handler: intercepts keys regardless of which child has focus
    private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_pendingChoice != null && !IsModifierKey(e.Key))
        {
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

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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

    private static bool IsModifierKey(Key key) => key is
        Key.LeftShift or Key.RightShift or
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.System or Key.LWin or Key.RWin or Key.CapsLock;

    private async Task SendMessage()
    {
        if (_isStreaming || _chatService == null) return;

        var userText = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(userText)) return;

        InputBox.Text = "";
        _isStreaming = true;

        AddBubble(userText, isUser: true);
        _currentBubble = AddBubble("", isUser: false);
        _currentBubbleHasContent = false;
        _typing = new TypingIndicator(_currentBubble);
        _typing.Start();

        try
        {
            await foreach (var chunk in _chatService.SendMessageAsync(userText))
            {
                // accepted tool ran — open a fresh bubble for the post-tool response
                if (_needNewBubble)
                {
                    _needNewBubble = false;
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
        catch (Exception ex)
        {
            _typing.Stop();
            _currentBubble!.Text = $"[Error: {ex.Message}]";
            _currentBubble.Foreground = Brushes.IndianRed;
        }
        finally
        {
            _typing?.Dispose();
            _typing = null;
            _isStreaming = false;
        }
    }

    private TextBlock AddBubble(string text, bool isUser)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        var border = new Border
        {
            Child = textBlock,
            Background = new SolidColorBrush(isUser
                ? Color.FromRgb(0x0E, 0x63, 0x9C)
                : Color.FromRgb(0x2D, 0x2D, 0x2D)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            MaxWidth = 360,
            Margin = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        MessagesPanel.Children.Add(border);
        ScrollToBottom();
        return textBlock;
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

    private sealed class TypingIndicator : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private int _dotCount;
        private TextBlock _target;

        public TypingIndicator(TextBlock target)
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

        public void Retarget(TextBlock newTarget)
        {
            _timer.Stop();
            _dotCount = 0;
            _target = newTarget;
            _timer.Start();
        }

        public void Dispose() => _timer.Stop();
    }
}

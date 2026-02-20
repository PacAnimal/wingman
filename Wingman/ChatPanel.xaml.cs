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
        InputBox.LostFocus += (_, _) =>
        {
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

    private void ActivatePending(Border card)
    {
        _activeCard = card;
        card.BorderThickness = new Thickness(1);
        card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
        _savedCaretBrush = InputBox.CaretBrush;
        InputBox.CaretBrush = Brushes.Transparent;
    }

    private void DeactivatePending()
    {
        _activeCard = null;
        InputBox.CaretBrush = _savedCaretBrush;
        _savedCaretBrush = null;
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
        DeactivatePending();
        cb?.Invoke(accept);
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
        DeactivatePending();
        cb?.Invoke(selected);
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

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // pending choice takes priority — digit selects, any other key aborts (without suppressing)
        if (_pendingChoice != null && !IsModifierKey(e.Key))
        {
            var digit = KeyToDigit(e.Key);
            if (digit >= 1 && digit <= _pendingChoiceOptions!.Length)
            {
                ResolveChoice(_pendingChoiceOptions[digit - 1]);
                e.Handled = true;
                return;
            }
            ResolveChoice(null);
            if (e.Key != Key.Enter)
                return;
        }

        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                // Shift+Enter: accept pending or do nothing
                if (_pendingApproval != null)
                    ResolveApproval(true);
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                // ctrl+enter: reject pending if any, then insert newline
                if (_pendingApproval != null)
                    ResolveApproval(false);
                var idx = InputBox.CaretIndex;
                InputBox.Text = InputBox.Text.Insert(idx, "\r\n");
                InputBox.CaretIndex = idx + 2;
                e.Handled = true;
                return;
            }

            // plain enter: reject pending if any, then send
            if (_pendingApproval != null)
                ResolveApproval(false);
            e.Handled = true;
            _ = SendMessage();
            return;
        }

        // any non-enter key rejects pending (ignore standalone modifier keys)
        if (_pendingApproval != null && !IsModifierKey(e.Key))
            ResolveApproval(false);
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
        var assistantBlock = AddBubble("", isUser: false);

        using var typing = new TypingIndicator(assistantBlock);
        typing.Start();

        try
        {
            var hasContent = false;
            await foreach (var chunk in _chatService.SendMessageAsync(userText))
            {
                // tool executed between chunks — open a fresh bubble for the post-tool response
                if (_needNewBubble)
                {
                    _needNewBubble = false;
                    if (hasContent)
                    {
                        assistantBlock = AddBubble("", isUser: false);
                        typing.Retarget(assistantBlock);
                        hasContent = false;
                    }
                }

                if (!hasContent)
                {
                    typing.Stop();
                    assistantBlock.Text = "";
                    hasContent = true;
                }
                assistantBlock.Text += chunk;
                ScrollToBottom();
            }
        }
        catch (Exception ex)
        {
            typing.Stop();
            assistantBlock.Text = $"[Error: {ex.Message}]";
            assistantBlock.Foreground = Brushes.IndianRed;
        }
        finally
        {
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

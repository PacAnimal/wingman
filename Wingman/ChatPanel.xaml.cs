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
    private TaskCompletionSource<bool>? _pendingApproval;
    private Action<bool>? _pendingApprovalCallback;

    public ChatPanel()
    {
        InitializeComponent();
        InputBox.LostFocus += (_, _) => { if (_pendingApproval != null) ResolveApproval(false); };
    }

    public void Initialize(IChatService? chatService)
    {
        _chatService = chatService;
        if (chatService == null)
            DisabledOverlay.Visibility = Visibility.Visible;
    }

    public void SetPendingApproval(TaskCompletionSource<bool> tcs, Action<bool> onResolved)
    {
        _pendingApproval = tcs;
        _pendingApprovalCallback = onResolved;
    }

    private void ResolveApproval(bool accept)
    {
        var tcs = _pendingApproval;
        var cb = _pendingApprovalCallback;
        _pendingApproval = null;
        _pendingApprovalCallback = null;
        cb?.Invoke(accept);
        tcs?.TrySetResult(accept);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _chatService?.ClearHistory();
        MessagesPanel.Children.Clear();
    }

    public TextBox InputTextBox => InputBox;

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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

        // cycling dots while waiting for first token
        var dotCount = 0;
        var typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        typingTimer.Tick += (_, _) =>
        {
            dotCount = dotCount % 3 + 1;
            assistantBlock.Text = new string('.', dotCount);
        };
        typingTimer.Start();

        try
        {
            var hasContent = false;
            await foreach (var chunk in _chatService.SendMessageAsync(userText))
            {
                if (!hasContent)
                {
                    typingTimer.Stop();
                    assistantBlock.Text = "";
                    hasContent = true;
                }
                assistantBlock.Text += chunk;
                ScrollToBottom();
            }
        }
        catch (Exception ex)
        {
            typingTimer.Stop();
            assistantBlock.Text = $"[Error: {ex.Message}]";
            assistantBlock.Foreground = Brushes.IndianRed;
        }
        finally
        {
            typingTimer.Stop();
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
}

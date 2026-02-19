using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wingman;

public partial class ChatPanel : UserControl
{
    private IChatService? _chatService;
    private bool _isStreaming;

    public ChatPanel()
    {
        InitializeComponent();
    }

    public void Initialize(IChatService? chatService)
    {
        _chatService = chatService;
        if (chatService == null)
            DisabledOverlay.Visibility = Visibility.Visible;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _chatService?.ClearHistory();
        MessagesPanel.Children.Clear();
    }

    public TextBox InputTextBox => InputBox;

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return; // TextBox handles shift+enter natively

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // TextBox doesn't handle ctrl+enter natively — insert newline manually
            var idx = InputBox.CaretIndex;
            InputBox.Text = InputBox.Text.Insert(idx, "\r\n");
            InputBox.CaretIndex = idx + 2;
            e.Handled = true;
            return;
        }

        e.Handled = true;
        _ = SendMessage();
    }

    private async Task SendMessage()
    {
        if (_isStreaming || _chatService == null) return;

        var userText = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(userText)) return;

        InputBox.Text = "";
        _isStreaming = true;

        AddBubble(userText, isUser: true);
        var assistantBlock = AddBubble("", isUser: false);

        try
        {
            await foreach (var chunk in _chatService.SendMessageAsync(userText))
            {
                assistantBlock.Text += chunk;
                ScrollToBottom();
            }
        }
        catch (Exception ex)
        {
            assistantBlock.Text += $"\n[Error: {ex.Message}]";
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

    private void ScrollToBottom() => MessagesScrollViewer.ScrollToEnd();
}

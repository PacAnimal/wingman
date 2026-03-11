using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wingman;

public interface IApprovalUi
{
    Task<bool> RequestApprovalAsync(string command, string purpose, string reason);
}

public class ApprovalUi(ChatPanel chatPanel, AgentEvents events) : IApprovalUi
{
    public async Task<bool> RequestApprovalAsync(string command, string purpose, string reason)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        events.RaiseCardWaitStarted();
        try
        {
            _ = chatPanel.Dispatcher.InvokeAsync(() =>
            {
                var commandBlock = new TextBlock
                {
                    Text = command,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                    TextWrapping = TextWrapping.Wrap
                };

                var purposeBlock = new TextBlock
                {
                    Text = purpose,
                    FontStyle = FontStyles.Italic,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                var reasonBlock = new TextBlock
                {
                    Text = reason,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Margin = new Thickness(0, 4, 0, 0)
                };

                var hintBlock = new TextBlock
                {
                    Text = "Shift+Enter to accept · any other key to reject",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                    FontSize = 10,
                    Margin = new Thickness(0, 8, 0, 0)
                };

                var inner = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 4, 0, 4),
                    Child = commandBlock
                };

                var stack = new StackPanel();
                stack.Children.Add(inner);
                stack.Children.Add(purposeBlock);
                stack.Children.Add(reasonBlock);
                stack.Children.Add(hintBlock);

                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 8, 10, 8),
                    MaxWidth = chatPanel.BubbleMaxWidth,
                    Margin = new Thickness(8, 4, 8, 4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Tag = "bubble",
                    Child = stack
                };

                chatPanel.SetPendingApproval(tcs, accepted =>
                {
                    chatPanel.RemoveElement(card);
                    chatPanel.InsertElement(new TextBlock
                    {
                        Text = $"{(accepted ? "Accepted" : "Rejected")}: {command}",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                        FontSize = 11,
                        Margin = new Thickness(10, 2, 8, 2),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        TextWrapping = TextWrapping.Wrap
                    });
                }, card);

                chatPanel.InsertElement(card);
            });

            return await tcs.Task;
        }
        finally
        {
            events.RaiseCardWaitEnded();
        }
    }
}

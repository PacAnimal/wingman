using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.AI;

namespace Wingman;

public class AskUserTool(Lazy<ChatPanel> chatPanel, AgentEvents events) : IAgentTool
{
    public AIFunction AsAIFunction() => AIFunctionFactory.Create(
        (string question, string[] options) => AskAsync(question, options),
        "ask_user",
        "Presents a numbered multiple-choice question to the user and waits for a single keypress to select an option. " +
        "Use when multiple equally valid approaches exist or when offering installation of a missing tool.");

    private Task<string> AskAsync(string question, string[] options)
    {
        if (options.Length < 1 || options.Length > 9)
            return Task.FromResult("Error: options must contain between 1 and 9 items.");

        events.RaiseToolStarted();
        events.RaiseCardWaitStarted();

        var panel = chatPanel.Value;
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        panel.Dispatcher.InvokeAsync(() =>
        {
            var stack = new StackPanel();

            // question
            stack.Children.Add(new TextBlock
            {
                Text = question,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });

            // numbered options
            for (var i = 0; i < options.Length; i++)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"[{i + 1}] {options[i]}",
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            // hint
            stack.Children.Add(new TextBlock
            {
                Text = $"Press 1\u2013{options.Length} to select / any other key to abort",
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                FontSize = 10,
                Margin = new Thickness(0, 8, 0, 0)
            });

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                MaxWidth = panel.BubbleMaxWidth,
                Margin = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = "bubble",
                Child = stack
            };

            panel.SetPendingChoice(tcs, selected =>
            {
                panel.RemoveElement(card);
                panel.InsertElement(new TextBlock
                {
                    Text = selected != null ? $"Selected: {selected}" : "Operation aborted",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                    FontSize = 11,
                    Margin = new Thickness(10, 2, 8, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    TextWrapping = TextWrapping.Wrap
                });
            }, options, card);

            panel.InsertElement(card);
        });

        return tcs.Task.ContinueWith(t =>
        {
            events.RaiseCardWaitEnded();
            var selection = t.Result;
            events.RaiseToolResult(selection != null ? $"[tool] asked user — chose \"{selection}\"" : "[tool] asked user — no selection");
            return selection ?? "User aborted — they may want to provide further input instead";
        });
    }
}

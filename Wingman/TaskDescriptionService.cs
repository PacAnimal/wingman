using System.Text;
using System.Windows.Threading;
using Microsoft.Extensions.AI;

namespace Wingman;

sealed class TaskDescriptionService : IDisposable
{
    private IChatClient? _client;
    private IChatService? _chat;
    private IScreenBuffer? _screen;
    private readonly DispatcherTimer _typingTimer;
    private DispatcherTimer? _firstCommandTimer;
    private bool _firstCommandFired;
    private bool _disposed;

    public event Action<string?>? TaskChanged;
    public string? CurrentTask { get; private set; }

    public TaskDescriptionService()
    {
        _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Constants.TaskTypingIntervalMinutes) };
        _typingTimer.Tick += async (_, _) => await GenerateAsync();
    }

    public void Start(IChatClient client, IChatService chat, IScreenBuffer screen)
    {
        _client = client;
        _chat = chat;
        _screen = screen;
    }

    public void OnUserTyping()
    {
        if (_client == null) return;
        _typingTimer.Stop();
        _typingTimer.Start();
    }

    public void OnFirstCommandCompleted()
    {
        if (_firstCommandFired || _client == null) return;
        _firstCommandFired = true;
        _firstCommandTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Constants.TaskFirstCommandDelaySeconds) };
        _firstCommandTimer.Tick += async (_, _) =>
        {
            _firstCommandTimer.Stop();
            await GenerateAsync();
        };
        _firstCommandTimer.Start();
    }

    public void Reset()
    {
        _typingTimer.Stop();
        _firstCommandTimer?.Stop();
        _firstCommandTimer = null;
        _firstCommandFired = false;
        CurrentTask = null;
        TaskChanged?.Invoke(null);
    }

    private async Task GenerateAsync()
    {
        if (_client == null || _chat == null || _screen == null) return;
        try
        {
            // last ~10 messages (skip system prompt), truncate each to 500 chars
            var history = _chat.History
                .Skip(1)
                .TakeLast(10)
                .Select(m =>
                {
                    var text = m.Text ?? string.Empty;
                    return $"{m.Role}: {(text.Length > 500 ? text[..500] : text)}";
                });

            var terminal = _screen.GetVisibleText();
            if (terminal.Length > 1000) terminal = terminal[^1000..];

            var sb = new StringBuilder();
            sb.AppendLine("Based on this conversation and terminal state, give a 2-5 word lowercase task description. Reply with ONLY the description, nothing else.");
            sb.AppendLine();
            sb.AppendLine("Conversation:");
            foreach (var line in history) sb.AppendLine(line);
            sb.AppendLine();
            sb.AppendLine("Terminal:");
            sb.AppendLine(terminal);

            var messages = new List<ChatMessage> { new(ChatRole.User, sb.ToString()) };
            var response = await _client.GetResponseAsync(messages);
            var task = response.Text?.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(task))
            {
                CurrentTask = task;
                TaskChanged?.Invoke(task);
            }
        }
        catch
        {
            // best-effort
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _typingTimer.Stop();
        _firstCommandTimer?.Stop();
    }
}

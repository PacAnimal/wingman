using System.Text;
using System.Windows.Threading;
using Microsoft.Extensions.AI;

namespace Wingman;

sealed class TaskDescriptionService : IDisposable
{
    private static readonly ChatOptions TitleOptions = new() { Temperature = 0f, MaxOutputTokens = 50 };

    private IChatClient? _client;
    private IChatService? _chat;
    private IScreenBuffer? _screen;
    private readonly DispatcherTimer _typingTimer;
    private TaskCompletionSource _firstCommandTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _firstCommandCts;
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
        _firstCommandCts = new CancellationTokenSource();
        _ = WaitForFirstCommandAsync(_firstCommandCts.Token);
    }

    public void SignalFirstCommand() => _firstCommandTcs.TrySetResult();

    public void OnUserTyping()
    {
        if (_client == null) return;
        _typingTimer.Stop();
        _typingTimer.Start();
    }

    public void Reset()
    {
        _typingTimer.Stop();
        _firstCommandCts?.Cancel();
        _firstCommandCts?.Dispose();
        _firstCommandTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _firstCommandCts = new CancellationTokenSource();
        _ = WaitForFirstCommandAsync(_firstCommandCts.Token);
        CurrentTask = null;
        TaskChanged?.Invoke(null);
    }

    private async Task WaitForFirstCommandAsync(CancellationToken ct)
    {
        try
        {
            await _firstCommandTcs.Task.WaitAsync(ct);
            await GenerateAsync();
        }
        catch (OperationCanceledException)
        {
            // reset or dispose cancelled us
        }
    }

    private async Task GenerateAsync()
    {
        if (_client == null || _chat == null || _screen == null) return;
        try
        {
            // last ~10 messages (skip system prompt + per-turn injections), truncate each to 500 chars
            var history = _chat.History
                .Skip(1)
                .Where(m => !ChatService.IsPerTurnMessage(m))
                .TakeLast(10)
                .Select(m =>
                {
                    var text = m.Text;
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
            var response = await _client.GetResponseAsync(messages, TitleOptions);
            var task = response.Text.Trim().ToLowerInvariant();
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
        _firstCommandCts?.Cancel();
        _firstCommandCts?.Dispose();
    }
}

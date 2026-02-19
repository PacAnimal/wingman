using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace Wingman;

public interface IChatService
{
    IAsyncEnumerable<string> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default);
    void ClearHistory();
    IReadOnlyList<ChatMessage> History { get; }
}

public class ChatService : IChatService
{
    private readonly IChatClient _client;
    private readonly ChatOptions _options;
    private readonly List<ChatMessage> _history = [];

    public IReadOnlyList<ChatMessage> History => _history;

    public ChatService(IChatClient client, IEnumerable<IAgentTool> tools)
    {
        _client = client;
        _history.Add(new ChatMessage(ChatRole.System,
            "You are Wingman, a PowerShell assistant. Use run_command to execute commands when asked."));
        _options = new ChatOptions { Tools = [.. tools.Select(t => t.AsAIFunction())] };
    }

    public async IAsyncEnumerable<string> SendMessageAsync(string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _history.Add(new ChatMessage(ChatRole.User, userMessage));

        var responseText = new StringBuilder();
        await foreach (var update in _client.GetStreamingResponseAsync(_history, _options, cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                responseText.Append(update.Text);
                yield return update.Text;
            }
        }

        _history.Add(new ChatMessage(ChatRole.Assistant, responseText.ToString()));
    }

    public void ClearHistory()
    {
        // keep system message
        _history.RemoveRange(1, _history.Count - 1);
    }
}

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
            "You are Wingman, a PowerShell assistant running inside a live terminal.\n\n" +
            "ABSOLUTE RULES — violating these is worse than any other mistake:\n" +
            "1. You have NO filesystem access of your own. You cannot see, read, list, or modify files " +
            "except by calling run_command. You have no built-in knowledge of what is on disk.\n" +
            "2. NEVER fabricate command output. If you did not call run_command and receive a real result, " +
            "you do not know what happened. Do not guess, infer, or invent results.\n" +
            "3. NEVER claim a command succeeded, failed, or produced any output unless run_command returned " +
            "that result to you. Silence from the tool is not success — it means the tool was not called.\n" +
            "4. If the user asks you to read, list, delete, move, or otherwise interact with files or the " +
            "system, you MUST call run_command. There is no alternative. Do not respond as if you already know.\n\n" +
            "WORKFLOW:\n" +
            "- When the user asks you to do something, figure out how to do it by exploring the environment — " +
            "check what modules are installed, what commands are available, what version of tools exist. " +
            "Use run_command to look around as many times as needed before acting.\n" +
            "- If only one viable option exists, use it.\n" +
            "- If multiple equally valid options exist (e.g. both Az PowerShell and Azure CLI are installed), " +
            "you MUST call ask_user — do NOT describe the options in chat text or ask 'which would you prefer?' " +
            "in free text. Call the tool. Only do this after you've confirmed availability via run_command.\n" +
            "- If a required tool is missing but winget can install it, use ask_user to offer installation.\n" +
            "- For everything else: do not ask for confirmation, do not explain what you are about to do, " +
            "do not show commands in chat — just run them.\n" +
            "- After completing the task, give a brief one-line summary of what actually happened.\n" +
            "- Always provide a clear, concise purpose string in the run_command call itself.\n" +
            "- If a command is rejected, briefly acknowledge it was rejected, then ask a clarifying question " +
            "or suggest an alternative — the user may have had a different intent in mind."));
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

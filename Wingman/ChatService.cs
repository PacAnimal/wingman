using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Cathedral.Utils;
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
    private readonly Func<IChatClient> _clientFactory;
    private readonly IChatClient _guardClient;
    private readonly ITerminal _terminal;
    private readonly SessionTracker _sessions;
    private readonly ChatOptions _options;
    private readonly SemaphoreSlimValue<List<ChatMessage>> _history = new([], disposeValue: false);
    private IChatClient _client;

    // context summarization state
    private string? _cachedSummary;
    private int _summarizedUpToIndex;

    public IReadOnlyList<ChatMessage> History
    {
        get
        {
            using var h = _history.WaitForDisposable().GetAwaiter().GetResult();
            return [.. h.Value];
        }
    }

    public ChatService(Func<IChatClient> clientFactory, IChatClient guardClient, IEnumerable<IAgentTool> tools, string memoryBlock, ITerminal terminal, SessionTracker sessions, bool supportsWebSearch = true)
    {
        _clientFactory = clientFactory;
        _guardClient = guardClient;
        _terminal = terminal;
        _sessions = sessions;
        _client = clientFactory();
        using var init = _history.WaitForDisposable().GetAwaiter().GetResult();
        init.Value.Add(new ChatMessage(ChatRole.System,
            "You are Wingman, a PowerShell assistant running inside a live terminal.\n\n" +
            "ABSOLUTE RULES — violating these is worse than any other mistake:\n" +
            "1. NEVER fabricate command output. If you did not call a tool and receive a real result, " +
            "you do not know what happened. Do not guess, infer, or invent results.\n" +
            "2. NEVER claim a command succeeded, failed, or produced any output unless run_command returned " +
            "that result to you. Silence from the tool is not success — it means the tool was not called.\n" +
            "3. NEVER respond as if you already know the contents of a file or directory unless you called " +
            "a tool and received the actual data. Do not guess, infer, or invent filesystem contents.\n\n" +
            "FILESYSTEM TOOLS — use these instead of run_command when just reading:\n" +
            "- list_directory: instant directory listing — prefer over `Get-ChildItem` in run_command.\n" +
            "- read_file: reads a text file and returns each line prefixed with its 1-based line number. " +
            "Supports offset/limit for paging large files. " +
            "Binary files (images, executables, archives, etc.) are refused — only text files are supported. " +
            "Prefer over `Get-Content` in run_command. Sensitive paths (credentials, keys, etc.) require user approval.\n" +
            "- write_file: writes text/config content to a file. Writing to $WMTMP is instant; " +
            "writing elsewhere requires user approval. " +
            "NEVER use write_file for scripts — run each command individually via run_command instead.\n" +
            "- edit_file: surgically edits a file using the line numbers shown by read_file. " +
            "Parameters: line (1-based, where to act), replaceLines (lines to remove starting at 'line'; 0 = insert only), " +
            "replaceWith (array of new lines to insert; always required — pass [] to only delete). " +
            "Prefer over read_file + write_file for targeted edits. Same approval rules as write_file.\n\n" +
            "COMMAND STYLE:\n" +
            "- Prefer native PowerShell cmdlets over compatibility aliases or CLI tools. " +
            "Use Get-ChildItem, not ls or dir. Use Connect-AzAccount, not `az login`. " +
            "Use the PowerShell equivalent unless the user explicitly asks for the other form, " +
            "or no PowerShell equivalent exists.\n\n" +
            "SHELL RULES — violating these causes session state loss and is unacceptable:\n" +
            "4. NEVER wrap commands in `pwsh`, `powershell`, `cmd /c`, `bash`, or any sub-shell. " +
            "Commands run directly in the LIVE session — that IS the whole point. " +
            "Spawning a sub-process loses session state: logins, variables, module imports, current directory.\n" +
            "5. If a command produces error output, treat it as failed even if exitCode is 0. " +
            "Do not proceed based on partial or garbled output — fix the command and retry.\n" +
            "6. Run ONE command per run_command call. Never chain with `;`, `&&`, `|`, or newlines unless " +
            "the pipe is the actual operation (e.g. `Get-Content file | Select-String pattern`). " +
            "If you need to cd and then run something, that is two calls — cd first, verify it worked, then run the next command.\n\n" +
            "WORKFLOW:\n" +
            "- When the user asks you to do something, figure out how to do it by exploring the environment — " +
            "check what modules are installed, what commands are available, what version of tools exist. " +
            "Use list_directory and read_file to browse and read; use run_command for execution.\n" +
            "- If only one viable option exists, use it.\n" +
            "- If multiple equally valid options exist (e.g. both Az PowerShell and Azure CLI are installed), " +
            "you MUST call ask_user — do NOT describe the options in chat text or ask 'which would you prefer?' " +
            "in free text. Call the tool. Only do this after you've confirmed availability via run_command.\n" +
            "- If a required tool is missing but winget can install it, use ask_user to offer installation.\n" +
            "- For everything else: do not ask for confirmation, do not explain what you are about to do, " +
            "do not show commands in chat — just run them.\n" +
            "- After completing the task, give a brief one-line summary of what actually happened. " +
            "Do NOT repeat or quote raw command output — the user can see the terminal. " +
            "Summaries, findings, and conclusions are welcome; verbatim output is not.\n" +
            "- Always provide a clear, concise purpose string in the run_command call itself.\n" +
            "- run_command returns structured JSON with: command, output, exitCode, success, workingDirectory, truncated, and duration. " +
            "Check exitCode and success to determine if a command succeeded. " +
            "If truncated is true, output was cut at 65,536 characters — consider re-running with a more targeted command to get the full data you need.\n" +
            "- If a command is rejected, briefly acknowledge it was rejected, then ask a clarifying question " +
            "or suggest an alternative — the user may have had a different intent in mind.\n" +
            "- If you cannot find something the user referred to (a file, folder, Office 365 group, user, resource, etc.), " +
            "search broadly and find the closest match. Then use ask_user with the matched name and Yes/No options " +
            "to confirm before proceeding. Never silently assume a match or give up without searching.\n\n" +
            "SCRATCH DIRECTORY:\n" +
            "- $WMTMP is a per-session scratch directory for temporary files. Use it freely for intermediate work.\n" +
            "- NEVER change the value of $WMTMP — it is a constant set by Wingman.\n" +
            "- Clean up files you create in $WMTMP when you no longer need them.\n\n" +
            (supportsWebSearch
                ? "WEB SEARCH:\n" +
                  "- You have automatic access to web search. When answering questions or performing tasks, " +
                  "actively search the web to retrieve up-to-date information about tools, software versions, " +
                  "best practices, documentation, APIs, and similar topics.\n" +
                  "- Do not rely solely on your training data when current information matters — " +
                  "search first, then act on what you find.\n" +
                  "- You do not need to call any tool explicitly — the system searches automatically when your response requires it.\n\n"
                : "") +
            "MEMORY:\n" +
            "- You have persistent memory across sessions via save_memory, delete_memory, update_memory, and list_memory tools.\n" +
            "- After EVERY tool call that reveals environment facts, immediately save them if not already stored. " +
            "Do NOT wait to be asked — the whole point of memory is to avoid rediscovering things you already found.\n" +
            "- Save these categories of facts:\n" +
            "  - Azure resource groups, subscription IDs, tenant IDs\n" +
            "  - Cloud infrastructure layout: which resource groups exist, what resources they contain, regions, SKUs\n" +
            "  - Tool/binary locations and versions (e.g., az path, terraform version)\n" +
            "  - Server names, IP addresses, DNS entries\n" +
            "  - User accounts, group memberships, mailbox configs\n" +
            "  - Directory structures and project layouts\n" +
            "  - Working connection strings and endpoints\n" +
            "  - Module/cmdlet availability\n" +
            "- Keep memories short — one concise line per fact.\n" +
            "- NEVER save passwords, secrets, API keys, or tokens.\n" +
            "- If you discover a saved memory is inaccurate, delete or update it immediately.\n" +
            "- When you have 90+ memories, proactively prune: merge related memories, delete obsolete ones, " +
            "and compress verbose memories into concise facts.\n" +
            "- Do NOT save conversation-specific context — only durable environment facts and techniques.\n\n" +
            "SESSION AWARENESS:\n" +
            "- Before each of your turns, you receive an [active sessions] block listing all authenticated sessions in this terminal.\n" +
            "- ALWAYS check this list before attempting to log in, connect, or authenticate to any service. If a session already exists, DO NOT re-authenticate.\n" +
            "- If a command fails with an authentication or authorization error, the session may have expired — re-authenticate and retry.\n" +
            "- Sessions are tracked automatically from both your commands and the user's terminal activity." +
            (memoryBlock.Length == 0 ? "" : "\n\n" + memoryBlock)));
        var toolList = tools.Select(t => t.AsAiFunction()).Cast<AITool>().ToList();
        if (supportsWebSearch) toolList.Add(new HostedWebSearchTool());
        _options = new ChatOptions { Tools = toolList };
    }

    public async IAsyncEnumerable<string> SendMessageAsync(string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // drain and summarize outside the semaphore to avoid holding the lock during LLM call
        var userCommands = _terminal.DrainUserCommands();
        string? formattedCommands = null;
        if (userCommands.Count > 0)
        {
            await _sessions.ProcessUserCommandsAsync(userCommands);
            formattedCommands = await FormatUserCommandsAsync(userCommands, cancellationToken);
        }

        using (var h = await _history.WaitForDisposable(cancellationToken))
        {
            if (formattedCommands != null)
                h.Value.Add(new ChatMessage(ChatRole.System, formattedCommands));
            var sessionContext = _sessions.FormatForContext();
            if (sessionContext != null)
                h.Value.Add(new ChatMessage(ChatRole.System, sessionContext));
            h.Value.Add(new ChatMessage(ChatRole.User, userMessage));
        }

        await SummarizeIfNeeded(cancellationToken);

        List<ChatMessage> apiHistory;
        using (var h = await _history.WaitForDisposable(cancellationToken))
            apiHistory = BuildApiHistory(h.Value);

        var preCallCount = apiHistory.Count;
        var responseText = new StringBuilder();
        try
        {
            await foreach (var update in _client.GetStreamingResponseAsync(apiHistory, _options, cancellationToken))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    responseText.Append(update.Text);
                    yield return update.Text;
                }
            }
        }
        finally
        {
            // persist tool call/result messages MEAI accumulated, then save final assistant response
            var toolMessages = ExtractToolMessages(apiHistory, preCallCount).ToList();
            if (toolMessages.Count > 0 || responseText.Length > 0)
            {
                using var h = await _history.WaitForDisposable(cancellationToken);
                foreach (var msg in toolMessages)
                    h.Value.Add(msg);
                if (responseText.Length > 0)
                    h.Value.Add(new ChatMessage(ChatRole.Assistant, responseText.ToString()));
            }
        }
    }

    public void ClearHistory()
    {
        using (var h = _history.WaitForDisposable().GetAwaiter().GetResult())
        {
            // keep system message
            h.Value.RemoveRange(1, h.Value.Count - 1);
        }
        _cachedSummary = null;
        _summarizedUpToIndex = 0;
        _sessions.Clear();
        // fresh client breaks the Responses API response chain (server-side state)
        _client = _clientFactory();
    }

    private static void AppendCommand(StringBuilder sb, UserCommandInfo cmd)
    {
        var status = cmd.Success ? "ok" : $"failed (exit {cmd.ExitCode})";
        sb.Append($"> {cmd.Command} — {status}, cwd: {cmd.WorkingDirectory}");
        if (!string.IsNullOrWhiteSpace(cmd.Output))
        {
            var lines = cmd.Output.Split('\n');
            if (lines.Length <= 10)
                sb.Append('\n').Append(cmd.Output.TrimEnd());
            else
                sb.Append($" ({lines.Length} lines)");
        }
        sb.Append('\n');
    }

    private static string FormatUserCommands(List<UserCommandInfo> commands)
    {
        var sb = new StringBuilder("[user terminal activity]\n");
        foreach (var cmd in commands)
            AppendCommand(sb, cmd);
        return sb.ToString().TrimEnd();
    }

    // formats old commands and caps at UserCommandSummarizeMaxInputChars, truncating oldest first
    private static string FormatOldCommands(List<UserCommandInfo> commands)
    {
        var sb = new StringBuilder();
        foreach (var cmd in commands)
            AppendCommand(sb, cmd);

        var text = sb.ToString();
        if (text.Length <= Constants.UserCommandSummarizeMaxInputChars)
            return text;

        // truncate from the start (oldest), snap to newline boundary
        var trimmed = text[(text.Length - Constants.UserCommandSummarizeMaxInputChars)..];
        var nl = trimmed.IndexOf('\n');
        if (nl >= 0) trimmed = trimmed[(nl + 1)..];
        return "[...earlier commands omitted...]\n" + trimmed;
    }

    private async Task<string> SummarizeOldCommandsAsync(List<UserCommandInfo> commands, CancellationToken ct)
    {
        var input = FormatOldCommands(commands);
        var prompt = $"Summarize these {commands.Count} terminal commands — what was done, what succeeded/failed, final cwd. Be concise.\n\n{input}";

        using var timeout = new CancellationTokenSource(Constants.GuardTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        var summarizeOptions = new ChatOptions { MaxOutputTokens = 2000 };
        var response = await _guardClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            summarizeOptions,
            linked.Token);

        return response.Text;
    }

    // fallback: keep tail of old commands capped at UserCommandFallbackMaxChars
    private static string TruncateOldCommands(List<UserCommandInfo> commands)
    {
        var sb = new StringBuilder();
        foreach (var cmd in commands)
            AppendCommand(sb, cmd);

        var text = sb.ToString();
        if (text.Length <= Constants.UserCommandFallbackMaxChars)
            return "[summary unavailable]\n" + text;

        var trimmed = text[(text.Length - Constants.UserCommandFallbackMaxChars)..];
        var nl = trimmed.IndexOf('\n');
        if (nl >= 0) trimmed = trimmed[(nl + 1)..];
        return "[summary unavailable — showing most recent]\n" + trimmed;
    }

    private async Task<string> FormatUserCommandsAsync(List<UserCommandInfo> commands, CancellationToken ct)
    {
        if (commands.Count <= Constants.UserCommandSummarizeThreshold)
            return FormatUserCommands(commands);

        var oldCommands = commands[..^Constants.UserCommandSummarizeThreshold];
        var recentCommands = commands[^Constants.UserCommandSummarizeThreshold..];

        string summary;
        try
        {
            summary = await SummarizeOldCommandsAsync(oldCommands, ct);
            if (string.IsNullOrWhiteSpace(summary))
                summary = TruncateOldCommands(oldCommands);
        }
        catch
        {
            summary = TruncateOldCommands(oldCommands);
        }

        var recentText = FormatUserCommands(recentCommands);
        return $"[user terminal activity — {commands.Count} commands, oldest {oldCommands.Count} summarized]\n{summary}\n\n[recent commands]\n{recentText}";
    }

    private List<ChatMessage> BuildApiHistory(List<ChatMessage> history)
    {
        if (history.Count <= Constants.ContextSummarizeThreshold || _cachedSummary == null)
            return [.. history];

        var recentStart = history.Count - Constants.ContextRecentToKeep;
        var result = new List<ChatMessage>(Constants.ContextRecentToKeep + 2)
        {
            history[0], // system message
            new(ChatRole.Assistant, $"[Summary of earlier conversation]\n{_cachedSummary}"),
        };
        result.AddRange(history.GetRange(recentStart, history.Count - recentStart));
        return result;
    }

    private async Task SummarizeIfNeeded(CancellationToken ct)
    {
        List<ChatMessage> snapshot;
        using (var h = await _history.WaitForDisposable(ct))
            snapshot = [.. h.Value];

        if (snapshot.Count <= Constants.ContextSummarizeThreshold) return;

        var summarizeEnd = snapshot.Count - Constants.ContextRecentToKeep;
        if (summarizeEnd <= _summarizedUpToIndex) return;

        try
        {
            var sb = new StringBuilder();
            if (_cachedSummary != null)
                sb.AppendLine($"Previous summary:\n{_cachedSummary}\n");

            sb.AppendLine("Summarize the following conversation into a brief, factual summary that captures key decisions, facts discovered, and context. Be concise — aim for a few short paragraphs.");
            sb.AppendLine();

            for (var i = 1; i < summarizeEnd; i++)
            {
                var msg = snapshot[i];
                string text;
                var funcCalls = msg.Contents.OfType<FunctionCallContent>().ToList();
                var funcResults = msg.Contents.OfType<FunctionResultContent>().ToList();
                if (funcCalls.Count > 0)
                    text = "[called " + string.Join(", ", funcCalls.Select(f => f.Name)) + "]";
                else if (funcResults.Count > 0)
                    text = "[" + string.Join("; ", funcResults.Select(r => { var rs = ResultToString(r.Result); return rs.Length > 200 ? rs[..200] + "..." : rs; })) + "]";
                else
                    text = msg.Text;
                if (text.Length > 2000) text = text[..2000] + "...";
                sb.AppendLine($"{msg.Role}: {text}");
            }

            using var timeout = new CancellationTokenSource(Constants.GuardTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
            var summarizeOptions = new ChatOptions { MaxOutputTokens = 2000 };
            var response = await _guardClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, sb.ToString())],
                summarizeOptions,
                linked.Token);

            _cachedSummary = response.Text;
            _summarizedUpToIndex = summarizeEnd;
            // fresh client to break Responses API server-side chain after summary shift
            _client = _clientFactory();
        }
        catch
        {
            // summarization failed — continue without updating summary
        }
    }

    private static IEnumerable<ChatMessage> ExtractToolMessages(List<ChatMessage> apiHistory, int preCallCount)
    {
        for (var i = preCallCount; i < apiHistory.Count; i++)
        {
            var msg = apiHistory[i];
            if (msg.Role == ChatRole.Assistant)
            {
                // skip pure-text assistant messages — final response saved separately
                if (msg.Contents.OfType<FunctionCallContent>().Any())
                    yield return msg;
            }
            else if (msg.Role == ChatRole.Tool)
            {
                yield return TruncateFunctionResults(msg);
            }
        }
    }

    private static ChatMessage TruncateFunctionResults(ChatMessage msg)
    {
        var needsTruncation = msg.Contents.OfType<FunctionResultContent>()
            .Any(r => ResultToString(r.Result).Length > Constants.ToolResultMaxCharsInHistory);
        if (!needsTruncation) return msg;

        var newContents = msg.Contents
            .Select<AIContent, AIContent>(c =>
            {
                if (c is not FunctionResultContent frc) return c;
                var s = ResultToString(frc.Result);
                if (s.Length <= Constants.ToolResultMaxCharsInHistory) return frc;
                return new FunctionResultContent(frc.CallId, s[..Constants.ToolResultMaxCharsInHistory]);
            })
            .ToList();
        return new ChatMessage(msg.Role, newContents);
    }

    private static string ResultToString(object? result)
    {
        if (result == null) return "";
        if (result is string s) return s;
        return JsonSerializer.Serialize(result);
    }
}

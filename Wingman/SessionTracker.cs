using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Wingman;

public record AuthSession(string Service, string Identity, DateTime EstablishedUtc);

public class SessionTracker(IChatClient guardClient, ILogger<SessionTracker> logger)
{
    private const int ExtractionTimeoutMs = 10_000;
    private const int MaxOutputCharsForExtraction = 2_000;
    private const int MaxOutputCharsPerBatchCmd = 500;

    private const string ExtractionSystemPrompt =
        "You are a terminal session analyzer. Extract authentication details from PowerShell commands and their output. " +
        "Respond only with the requested JSON — no explanation, no markdown.";

    private readonly Dictionary<string, AuthSession> _sessions = [];
    private readonly Lock _lock = new();

    public IReadOnlyDictionary<string, AuthSession> Sessions
    {
        get { lock (_lock) return new Dictionary<string, AuthSession>(_sessions); }
    }

    public async Task ProcessAuthCommandAsync(string command, CommandResult result)
    {
        var output = result.Output.Length > MaxOutputCharsForExtraction
            ? result.Output[..MaxOutputCharsForExtraction]
            : result.Output;

        var prompt =
            $$"""
            This authentication command just ran in a terminal. Extract:
            - type: "login" if connecting/authenticating, "logout" if disconnecting, "failed" if it clearly failed
            - service: the service or system connected to (e.g. "azure", "exchange-online", "microsoft-graph", "ssh:hostname", "kubernetes")
            - identity: the account logged in as — email address, UPN, username, or context name (empty string if not shown)

            Command: {{command}}
            Output: {{output}}

            Example: {"type": "login", "service": "azure", "identity": "user@contoso.com"}
            """;

        try
        {
            using var cts = new CancellationTokenSource(ExtractionTimeoutMs);
            var options = new ChatOptions { MaxOutputTokens = 200, ResponseFormat = ChatResponseFormat.Json };
            var response = await guardClient.GetResponseAsync(
                [new ChatMessage(ChatRole.System, ExtractionSystemPrompt), new ChatMessage(ChatRole.User, prompt)],
                options,
                cts.Token);

            ApplyExtraction(response.Text, command);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auth extraction failed for command: {Command}", command);
        }
    }

    public async Task ProcessUserCommandsAsync(List<UserCommandInfo> commands)
    {
        if (commands.Count == 0) return;

        var sb = new StringBuilder(
            "These terminal commands were just executed. For each authentication command (login, logout, connect, disconnect), " +
            "extract the service connected to and the identity (account/email/UPN) used. Skip non-auth commands.\n\nCommands:\n");

        for (var i = 0; i < commands.Count; i++)
        {
            var cmd = commands[i];
            var status = cmd.Success ? "ok" : $"exit {cmd.ExitCode}";
            var truncated = cmd.Output.Length > MaxOutputCharsPerBatchCmd
                ? cmd.Output[..MaxOutputCharsPerBatchCmd]
                : cmd.Output;
            sb.AppendLine($"{i + 1}. {cmd.Command} ({status}, output: {truncated})");
        }

        sb.Append(
            "\nExample: [{\"index\": 1, \"type\": \"login\", \"service\": \"azure\", \"identity\": \"user@contoso.com\"}]\n" +
            "Return [] if no auth commands.");

        try
        {
            using var cts = new CancellationTokenSource(ExtractionTimeoutMs);
            var options = new ChatOptions { MaxOutputTokens = 500, ResponseFormat = ChatResponseFormat.Json };
            var response = await guardClient.GetResponseAsync(
                [new ChatMessage(ChatRole.System, ExtractionSystemPrompt), new ChatMessage(ChatRole.User, sb.ToString())],
                options,
                cts.Token);

            ApplyBatchExtractions(response.Text);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Batch auth extraction failed for {Count} user commands", commands.Count);
        }
    }

    public string? FormatForContext()
    {
        lock (_lock)
        {
            if (_sessions.Count == 0) return null;

            var sb = new StringBuilder("[active sessions — do not re-authenticate]\n");
            foreach (var (_, session) in _sessions)
            {
                var time = session.EstablishedUtc.ToLocalTime().ToString("HH:mm");
                sb.AppendLine($"- {session.Service}: {session.Identity} (since {time})");
            }

            return sb.ToString().TrimEnd();
        }
    }

    public void Clear()
    {
        lock (_lock) _sessions.Clear();
    }

    // extracts first {...} block from text; returns null if no object found
    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    // extracts first [...] block from text
    private static string ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        return start >= 0 && end > start ? text[start..(end + 1)] : "[]";
    }

    private void ApplyExtraction(string responseText, string commandForLog)
    {
        logger.LogDebug("Auth extraction raw response: {Text}", responseText);
        if (string.IsNullOrWhiteSpace(responseText)) return;

        try
        {
            var json = ExtractJson(responseText);
            if (json == null) return;
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString() ?? "";
            var service = doc.RootElement.GetProperty("service").GetString() ?? "";
            var identity = doc.RootElement.GetProperty("identity").GetString() ?? "";

            if (string.IsNullOrWhiteSpace(service)) return;
            var key = service.ToLowerInvariant();

            lock (_lock)
            {
                switch (type)
                {
                    case "login":
                        _sessions[key] = new AuthSession(service, identity, DateTime.UtcNow);
                        if (string.IsNullOrEmpty(identity))
                            logger.LogDebug("Session recorded: {Service}", service);
                        else
                            logger.LogDebug("Session recorded: {Service} as {Identity}", service, identity);
                        break;
                    case "logout":
                        _sessions.Remove(key);
                        logger.LogDebug("Session removed: {Service}", service);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse auth extraction response for command: {Command}", commandForLog);
        }
    }

    private void ApplyBatchExtractions(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText)) return;

        try
        {
            var json = ExtractJsonArray(responseText);
            using var doc = JsonDocument.Parse(json);

            lock (_lock)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var type = item.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                    var service = item.TryGetProperty("service", out var sp) ? sp.GetString() ?? "" : "";
                    var identity = item.TryGetProperty("identity", out var ip) ? ip.GetString() ?? "" : "";

                    if (string.IsNullOrWhiteSpace(service)) continue;
                    var key = service.ToLowerInvariant();

                    switch (type)
                    {
                        case "login":
                            _sessions[key] = new AuthSession(service, identity, DateTime.UtcNow);
                            if (string.IsNullOrEmpty(identity))
                                logger.LogDebug("Session recorded (batch): {Service}", service);
                            else
                                logger.LogDebug("Session recorded (batch): {Service} as {Identity}", service, identity);
                            break;
                        case "logout":
                            _sessions.Remove(key);
                            logger.LogDebug("Session removed (batch): {Service}", service);
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse batch auth extraction response");
        }
    }
}

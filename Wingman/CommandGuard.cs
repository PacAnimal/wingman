using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Wingman;

public enum CommandVerdict { Accepted, NeedsReview }

public record GuardResult(CommandVerdict Verdict, string Reason);

public interface ICommandGuard
{
    Task<GuardResult> EvaluateAsync(string command, string purpose, CancellationToken ct = default);
}

public class CommandGuard(IChatClient client, ILogger<CommandGuard> logger) : ICommandGuard
{
    private const string SystemPrompt =
        """
        You are an expert PowerShell user and security watchdog for a PowerShell terminal. Your job is to protect the user from unintended side effects.
        Err heavily on the side of caution — when in doubt, always flag for review.

        Accept (respond with accept: true) ONLY commands that are unambiguously read-only and purely informational:
        - Pure queries: Get-*, ls, dir, cat, type, echo, whoami, hostname, pwd
        - Status checks: git status, git log, git diff, git branch
        - Build/test (read-only): dotnet build, dotnet test, dotnet run (read-only by nature)
        - Environment inspection: $env:*, [System.Environment]::GetEnvironmentVariable
        - Directory navigation: cd, Set-Location, Push-Location, Pop-Location (these only change the shell's working directory — they do NOT modify the filesystem or system state)

        Flag for review (respond with accept: false) everything else, including:
        - Any filesystem change: Remove-Item, rm, del, mkdir, cp, mv, New-Item, Rename-Item, Write-*, Set-Content, Out-File, Tee-Object, etc.
        - Any state mutation: Set-* (EXCEPT Set-Location), New-*, Stop-*, Start-*, Restart-*, Enable-*, Disable-*, Register-*, Unregister-*
        - Git writes: git commit, git push, git pull, git merge, git rebase, git reset, git checkout, git stash
        - Package/software changes: dotnet publish, npm install, winget, choco, pip install, etc.
        - Registry, permissions, environment variable writes
        - Pipelines that redirect output to files (>, >>)
        - Any command you are not completely certain is read-only

        If you have ANY doubt, flag for review. False positives are acceptable; false negatives are not.

        Respond ONLY with JSON: {"accept": true/false, "reason": "brief explanation"}
        """;

    public async Task<GuardResult> EvaluateAsync(string command, string purpose, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, $"Command: {command}\nPurpose: {purpose}")
            };
            var options = new ChatOptions { Temperature = 0, MaxOutputTokens = 150, ResponseFormat = ChatResponseFormat.Json };

            var response = await client.GetResponseAsync(messages, options, cts.Token);
            using var doc = JsonDocument.Parse(response.Text ?? "");
            var accept = doc.RootElement.GetProperty("accept").GetBoolean();
            var reason = doc.RootElement.GetProperty("reason").GetString() ?? "";

            return new GuardResult(accept ? CommandVerdict.Accepted : CommandVerdict.NeedsReview, reason);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Guard evaluation failed, defaulting to NeedsReview");
            return new GuardResult(CommandVerdict.NeedsReview, "Guard unavailable — manual review required");
        }
    }
}

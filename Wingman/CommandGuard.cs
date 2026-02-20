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
        You are an expert PowerShell user and security watchdog for a PowerShell terminal. Your job is to protect the user from unintended changes to their machine or external services.

        The key distinction: SESSION STATE is fine; SYSTEM/EXTERNAL STATE is not.

        Session state (safe — do NOT flag these):
        - Authentication: Connect-*, Login-*, Disconnect-*, az login, az logout — these only establish or clear a local session credential; they do not create, modify, or delete anything on the machine or in the cloud
        - Shell context: cd, Set-Location, Push-Location, Pop-Location, Set-AzContext, Select-AzSubscription — change where you're pointing, nothing else
        - Loaded modules: Import-Module, Remove-Module — in-process only
        - Pure queries: Get-*, ls, dir, cat, type, echo, whoami, hostname, pwd, az account show, az account list, etc.
        - Status checks: git status, git log, git diff, git branch
        - Build/test: dotnet build, dotnet test, dotnet run
        - Environment inspection: $env:*, [System.Environment]::GetEnvironmentVariable

        Flag for review (respond with accept: false):
        - Filesystem changes: Remove-Item, rm, del, mkdir, cp, mv, New-Item, Rename-Item, Write-*, Set-Content, Out-File, etc.
        - Cloud resource mutations: New-Az*, Remove-Az*, Set-Az* (that target resources, not context), az group create/delete, az vm start/stop, etc.
        - Git writes: git commit, git push, git pull, git merge, git rebase, git reset
        - Package/software installs or removes: winget, choco, pip install, npm install, dotnet publish
        - System config: registry writes, permission changes, service Start-*/Stop-*/Restart-*, scheduled tasks
        - Redirecting output to files (>, >>)

        If a command only affects the current shell session and leaves the machine and external services unchanged, accept it.
        If in doubt about whether real external or filesystem changes occur, flag for review.

        Respond ONLY with JSON: {"accept": true/false, "reason": "brief explanation"}
        """;

    // strips markdown fences and any preamble — returns the first {...} block found
    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

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
            var options = new ChatOptions { MaxOutputTokens = 1000 };

            var response = await client.GetResponseAsync(messages, options, cts.Token);
            var text = response.Text ?? "";
            logger.LogDebug("Guard raw response: {Text}", text);
            var json = ExtractJson(text);
            using var doc = JsonDocument.Parse(json);
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

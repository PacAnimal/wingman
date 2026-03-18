using Microsoft.Extensions.AI;

namespace Wingman;

public interface IAgentTool
{
    AIFunction AsAiFunction();
}

public class RunCommandTool(ITerminal terminal, ICommandGuard guard, Lazy<IApprovalUi> approvalUi, AgentEvents events, SessionTracker sessions) : IAgentTool
{
    public AIFunction AsAiFunction() => AIFunctionFactory.Create(
        (string command, string purpose) => ExecuteWithGuard(command, purpose),
        "run_command",
        "Executes a PowerShell command in the terminal and returns a structured result with output, exit code, success status, and working directory. Always provide a clear, concise purpose describing why the command is needed.");

    private async Task<CommandResult> ExecuteWithGuard(string command, string purpose)
    {
        events.RaiseToolStarted();
        var result = await guard.EvaluateAsync(command, purpose);

        CommandResult commandResult;
        if (result.Verdict == CommandVerdict.Accepted)
        {
            events.RaiseCommandStarting();
            commandResult = Completed(await terminal.RunCommand(command));
        }
        else
        {
            // needs review — show approval card and wait for user decision
            var approved = await approvalUi.Value.RequestApprovalAsync(command, purpose, result.Reason);
            if (!approved)
                return new CommandResult(command, "Command rejected by user", -1, false, "", false, "00:00:00");

            events.RaiseCommandStarting();
            commandResult = Completed(await terminal.RunCommand(command));
        }

        if (result.IsAuth)
            _ = sessions.ProcessAuthCommandAsync(command, commandResult);

        return commandResult;

        CommandResult Completed(CommandResult r)
        {
            var lines = r.Output.Length == 0 ? 0 : r.Output.Split('\n').Length;
            events.RaiseToolResult($"[tool] ran '{command}' — exit {r.ExitCode}, {lines} output lines, {r.Duration}");
            return r;
        }
    }
}

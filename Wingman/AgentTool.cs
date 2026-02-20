using Microsoft.Extensions.AI;

namespace Wingman;

public interface IAgentTool
{
    AIFunction AsAIFunction();
}

public class RunCommandTool(ITerminal terminal, ICommandGuard guard, Lazy<IApprovalUI> approvalUi, AgentEvents events) : IAgentTool
{
    public AIFunction AsAIFunction() => AIFunctionFactory.Create(
        (string command, string purpose, int timeoutMs = 30000) => ExecuteWithGuard(command, purpose, timeoutMs),
        "run_command",
        "Executes a PowerShell command in the terminal and returns a structured result with output, exit code, success status, and working directory. Always provide a clear, concise purpose describing why the command is needed.");

    private async Task<CommandResult> ExecuteWithGuard(string command, string purpose, int timeoutMs)
    {
        events.RaiseToolStarted();
        var result = await guard.EvaluateAsync(command, purpose);

        if (result.Verdict == CommandVerdict.Accepted)
            return await terminal.RunCommand(command, timeoutMs);

        // needs review — show approval card and wait for user decision
        var approved = await approvalUi.Value.RequestApprovalAsync(command, purpose, result.Reason);
        if (!approved)
            return new CommandResult(command, "Command rejected by user", -1, false, "");

        return await terminal.RunCommand(command, timeoutMs);
    }
}

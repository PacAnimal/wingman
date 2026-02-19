using Microsoft.Extensions.AI;

namespace Wingman;

public interface IAgentTool
{
    AIFunction AsAIFunction();
}

public class RunCommandTool(ITerminal terminal) : IAgentTool
{
    public AIFunction AsAIFunction() => AIFunctionFactory.Create(
        (string command, int timeoutMs = 30000) => terminal.RunCommand(command, timeoutMs),
        "run_command",
        "Executes a PowerShell command in the terminal and returns its output");
}

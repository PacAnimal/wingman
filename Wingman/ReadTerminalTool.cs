using Microsoft.Extensions.AI;

namespace Wingman;

public class ReadTerminalTool(IScreenBuffer screenBuffer, AgentEvents events) : IAgentTool
{
    public AIFunction AsAiFunction() => AIFunctionFactory.Create(
        () => ReadTerminal(),
        "read_terminal",
        "Returns the text currently visible in the terminal viewport. " +
        "Use this to see what the user sees — command output, prompts, " +
        "or output from long-running processes. Does not execute any command.");

    private string ReadTerminal()
    {
        events.RaiseToolStarted();
        var text = screenBuffer.GetVisibleText();
        var lines = text.Length == 0 ? 0 : text.Split('\n').Length;
        events.RaiseToolResult($"[tool] read terminal — {lines} visible lines");
        return text;
    }
}

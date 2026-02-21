using Microsoft.Extensions.AI;

namespace Wingman;

public class ReadTerminalTool(IScreenBuffer screenBuffer, AgentEvents events) : IAgentTool
{
    public AIFunction AsAIFunction() => AIFunctionFactory.Create(
        () => ReadTerminal(),
        "read_terminal",
        "Returns the text currently visible in the terminal viewport. " +
        "Use this to see what the user sees — command output, prompts, " +
        "or output from long-running processes. Does not execute any command.");

    private string ReadTerminal()
    {
        events.RaiseToolStarted();
        var text = screenBuffer.GetVisibleText();
        var lineCount = screenBuffer.LineCount;
        var viewportRows = screenBuffer.ViewportRows;
        return $"[buffer: {lineCount} total rows, viewport: {viewportRows} rows]\n{text}";
    }
}

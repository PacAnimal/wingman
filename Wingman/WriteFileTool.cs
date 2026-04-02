using System.IO;
using Microsoft.Extensions.AI;

namespace Wingman;

public class WriteFileTool(ITerminal terminal, Lazy<IApprovalUi> approvalUi, AgentEvents events) : IAgentTool
{
    public AIFunction AsAiFunction() => AIFunctionFactory.Create(
        (string path, string content) => WriteFileAsync(path, content),
        "write_file",
        "Writes text content to a file. Use for config files, notes, or structured text output — NOT for scripts. " +
        "Writing to $WMTMP (scratch directory) is instant; writing elsewhere requires user approval.");

    private async Task<string> WriteFileAsync(string path, string content)
    {
        events.RaiseToolActivity("Write " + path);

        var (sensitive, reason) = ReadFileTool.IsSensitivePath(path);
        var needsApproval = sensitive || !path.StartsWith(terminal.ScratchDir, StringComparison.OrdinalIgnoreCase);

        if (needsApproval)
        {
            var approvalReason = sensitive ? reason : "Writing outside scratch directory";
            var approved = await approvalUi.Value.RequestApprovalAsync(path, "Write file", approvalReason);
            if (!approved)
                return "File write rejected by user.";
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, content);
            events.RaiseToolResult($"[tool] wrote {content.Length} chars to {path}");
            return $"Written {content.Length} characters to {path}";
        }
        catch (UnauthorizedAccessException)
        {
            return $"Error: access denied: {path}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}

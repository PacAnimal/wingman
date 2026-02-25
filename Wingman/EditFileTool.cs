using System.IO;
using Microsoft.Extensions.AI;

namespace Wingman;

public class EditFileTool(ITerminal terminal, Lazy<IApprovalUI> approvalUi, AgentEvents events) : IAgentTool
{
    public AIFunction AsAIFunction() => AIFunctionFactory.Create(
        (string path, int line, int replaceLines, string[] replaceWith) => EditFileAsync(path, line, replaceLines, replaceWith),
        "edit_file",
        "Edits a text file at a specific line. " +
        "line: 1-based line number where the edit begins (use the line numbers shown by read_file). " +
        "replaceLines: how many lines starting at 'line' to remove (0 = pure insert, no removal). " +
        "replaceWith: array of new lines to insert at 'line' after removal (pass [] to only delete — always required). " +
        "Examples: replace line 5 → line=5, replaceLines=1, replaceWith=[\"new content\"]; " +
        "insert before line 3 → line=3, replaceLines=0, replaceWith=[\"inserted\"]; " +
        "delete 4 lines at 10 → line=10, replaceLines=4, replaceWith=[]. " +
        "Same approval rules as write_file.");

    private async Task<string> EditFileAsync(string path, int line, int replaceLines, string[] replaceWith)
    {
        events.RaiseToolActivity("Edit " + path);

        var (sensitive, reason) = ReadFileTool.IsSensitivePath(path);
        var needsApproval = sensitive || !path.StartsWith(terminal.ScratchDir, StringComparison.OrdinalIgnoreCase);

        if (needsApproval)
        {
            var approvalReason = sensitive ? reason : "Writing outside scratch directory";
            var approved = await approvalUi.Value.RequestApprovalAsync(path, "Edit file", approvalReason);
            if (!approved)
                return "File edit rejected by user.";
        }

        try
        {
            if (!File.Exists(path))
                return $"Error: file not found: {path}";

            var lines = new List<string>(await File.ReadAllLinesAsync(path));
            var total = lines.Count;

            // clamp to valid range; line can equal total+1 to append at end
            var idx = Math.Clamp(line - 1, 0, total);
            var remove = Math.Clamp(replaceLines, 0, total - idx);

            lines.RemoveRange(idx, remove);
            if (replaceWith.Length > 0)
                lines.InsertRange(idx, replaceWith);

            await File.WriteAllLinesAsync(path, lines);

            var action = (remove, replaceWith.Length) switch
            {
                (0, > 0) => $"inserted {replaceWith.Length} line(s) before line {line}",
                ( > 0, 0) => $"deleted {remove} line(s) at line {line}",
                _ => $"replaced {remove} line(s) at line {line} with {replaceWith.Length} line(s)",
            };
            events.RaiseToolResult($"[tool] edited {path}: {action}");
            return $"{path}: {action}";
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

using System.IO;
using System.Text;
using Microsoft.Extensions.AI;

namespace Wingman;

public class ListDirectoryTool(AgentEvents events) : IAgentTool
{
    public AIFunction AsAiFunction() => AIFunctionFactory.Create(
        (string path) => ListDirectory(path),
        "list_directory",
        "Lists the contents of a directory. Returns each entry prefixed with [DIR] or [FILE] with mime type and size, sorted directories first then alphabetically. Much faster than run_command for browsing the filesystem.");

    private string ListDirectory(string path)
    {
        events.RaiseToolActivity("List " + path);
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
                return $"Error: directory not found: {path}";

            var entries = dir.GetFileSystemInfos();
            var dirs = entries.OfType<DirectoryInfo>().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
            var files = entries.OfType<FileInfo>().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            foreach (var d in dirs)
                sb.AppendLine($"[DIR]  {d.Name}");
            foreach (var f in files)
            {
                var mime = SafeDetect(f.FullName);
                sb.AppendLine($"[FILE] {f.Name} ({mime}, {FormatSize(f.Length)})");
            }

            events.RaiseToolResult($"[tool] listed {path} — {entries.Length} entries");
            return sb.Length == 0 ? "(empty directory)" : sb.ToString().TrimEnd();
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

    private static string SafeDetect(string path)
    {
        try { return MimeDetector.Detect(path).MimeType; }
        catch { return "unknown"; }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };
}

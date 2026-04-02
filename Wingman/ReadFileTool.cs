using System.IO;
using System.Text;
using Microsoft.Extensions.AI;

namespace Wingman;

public class ReadFileTool(Lazy<IApprovalUi> approvalUi, AgentEvents events) : IAgentTool
{
    private const int DefaultLimit = 500;

    public AIFunction AsAiFunction() => AIFunctionFactory.Create(
        (string path, int offset, int limit) => ReadFileAsync(path, offset, limit),
        "read_file",
        "Reads a text file and returns its contents with line numbers. Hard limit of 500 lines per call — " +
        "use offset and limit to page through larger files. " +
        "Only works on text files; binary files (images, archives, executables) are refused with an error. " +
        "Much faster than run_command for reading files. Sensitive paths (credentials, keys, etc.) require user approval.");

    private async Task<string> ReadFileAsync(string path, int offset = 0, int limit = DefaultLimit)
    {
        events.RaiseToolActivity("Read " + path);

        var (sensitive, reason) = IsSensitivePath(path);
        if (sensitive)
        {
            var approved = await approvalUi.Value.RequestApprovalAsync(path, "Read file contents", reason);
            if (!approved)
                return "File read rejected by user.";
        }

        try
        {
            if (!File.Exists(path))
                return $"Error: file not found: {path}";

            try
            {
                var mime = MimeDetector.Detect(path);
                if (!mime.IsText)
                    return $"Error: {path} is a binary file ({mime.MimeType}). read_file only supports text files.";
            }
            catch
            {
                // detection failed — proceed; ReadAllLinesAsync will produce garbage on true binaries
            }

            var allLines = await File.ReadAllLinesAsync(path);
            var totalLines = allLines.Length;

            var effectiveOffset = Math.Max(0, Math.Min(offset, totalLines));
            var effectiveLimit = Math.Max(1, Math.Min(limit, DefaultLimit));
            var slice = allLines.Skip(effectiveOffset).Take(effectiveLimit).ToArray();

            var sb = new StringBuilder();
            for (var i = 0; i < slice.Length; i++)
                sb.AppendLine($"{effectiveOffset + i + 1,6}: {slice[i]}");

            var truncated = effectiveOffset + slice.Length < totalLines;
            if (truncated)
                sb.AppendLine($"\n[Showing lines {effectiveOffset + 1}–{effectiveOffset + slice.Length} of {totalLines}. Use offset/limit to read more.]");

            events.RaiseToolResult($"[tool] read {path} — {slice.Length} lines");
            return sb.ToString().TrimEnd();
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

    internal static (bool sensitive, string reason) IsSensitivePath(string path)
    {
        var fullPath = path.Replace('/', '\\');
        var lower = fullPath.ToLowerInvariant();
        var fileName = Path.GetFileName(lower);
        var ext = Path.GetExtension(lower);

        // sensitive directories
        if (lower.Contains(@"\appdata\") || lower.Contains(@"\application data\"))
            return (true, "Path is inside AppData");
        if (lower.Contains(@"\.ssh\") || lower.Contains(@"\.gnupg\") || lower.Contains(@"\.aws\") ||
            lower.Contains(@"\.azure\") || lower.Contains(@"\.gcp\") || lower.Contains(@"\.kube\"))
            return (true, "Path is inside a credentials directory");
        if (lower.Contains(@"\.wingman"))
            return (true, "Path contains Wingman settings");

        // sensitive filenames
        if (fileName == ".env" || fileName.StartsWith(".env.") || fileName.EndsWith(".env"))
            return (true, "File looks like an environment/secrets file");
        if (fileName.Contains("secret") || fileName.Contains("credential") || fileName.Contains("token") ||
            fileName.Contains("password") || fileName.Contains("apikey") || fileName.Contains("api_key") ||
            fileName.Contains("api-key"))
            return (true, "Filename suggests sensitive content");

        // sensitive extensions
        if (ext is ".key" or ".pem" or ".pfx" or ".p12" or ".keystore" or ".jks" or ".crt")
            return (true, $"File extension {ext} indicates a key or certificate");

        return (false, string.Empty);
    }
}

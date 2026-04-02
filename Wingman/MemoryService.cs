using System.Text;

namespace Wingman;

public interface IMemoryService
{
    Task<string> SaveMemoryAsync(string memory, CancellationToken ct = default);
    Task<string> DeleteMemoryAsync(int index, CancellationToken ct = default);
    Task<string> UpdateMemoryAsync(int index, string memory, CancellationToken ct = default);
    Task<string> ListMemoriesAsync(CancellationToken ct = default);
    Task<string> DeleteAllAsync(CancellationToken ct = default);
    Task<string> FormatForSystemPrompt(CancellationToken ct = default);
}

public class MemoryService(ISettingsService settings) : IMemoryService
{
    public async Task<string> SaveMemoryAsync(string memory, CancellationToken ct = default)
    {
        var memories = await settings.GetMemoriesAsync(ct);
        if (memories.Count >= Constants.MaxMemories)
            return $"Error: memory limit of {Constants.MaxMemories} reached. Delete or update existing memories first.";

        // duplicate detection (case-insensitive substring match)
        if (memories.Any(m => m.Contains(memory, StringComparison.OrdinalIgnoreCase)
                            || memory.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return "Error: duplicate memory (similar entry already exists).";

        var assignedIndex = 0;
        await settings.UpdateMemoriesAsync(list =>
        {
            list.Add(memory);
            assignedIndex = list.Count;
        }, ct);
        return $"Saved memory #{assignedIndex}: {memory}";
    }

    public async Task<string> DeleteMemoryAsync(int index, CancellationToken ct = default)
    {
        var memories = await settings.GetMemoriesAsync(ct);
        if (index < 1 || index > memories.Count)
            return $"Error: index {index} is out of range (1\u2013{memories.Count}).";

        var old = memories[index - 1];
        await settings.UpdateMemoriesAsync(list => list.RemoveAt(index - 1), ct);
        return $"Deleted memory #{index}: {old}";
    }

    public async Task<string> UpdateMemoryAsync(int index, string memory, CancellationToken ct = default)
    {
        var memories = await settings.GetMemoriesAsync(ct);
        if (index < 1 || index > memories.Count)
            return $"Error: index {index} is out of range (1\u2013{memories.Count}).";

        await settings.UpdateMemoriesAsync(list => list[index - 1] = memory, ct);
        return $"Updated memory #{index}: {memory}";
    }

    public async Task<string> ListMemoriesAsync(CancellationToken ct = default)
    {
        var memories = await settings.GetMemoriesAsync(ct);
        if (memories.Count == 0)
            return "(no memories)";

        var sb = new StringBuilder();
        for (var i = 0; i < memories.Count; i++)
            sb.AppendLine($"{i + 1}. {memories[i]}");
        return sb.ToString().TrimEnd();
    }

    public async Task<string> DeleteAllAsync(CancellationToken ct = default)
    {
        await settings.UpdateMemoriesAsync(list => list.Clear(), ct);
        return "All memories deleted.";
    }

    public async Task<string> FormatForSystemPrompt(CancellationToken ct = default)
    {
        var memories = await settings.GetMemoriesAsync(ct);
        if (memories.Count == 0)
            return string.Empty;

        var sb = new StringBuilder("YOUR SAVED MEMORIES — use these instead of re-running discovery commands:\n");
        for (var i = 0; i < memories.Count; i++)
            sb.AppendLine($"{i + 1}. {memories[i]}");
        return sb.ToString().TrimEnd();
    }
}

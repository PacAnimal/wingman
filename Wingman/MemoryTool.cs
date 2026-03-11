using Microsoft.Extensions.AI;

namespace Wingman;

public class SaveMemoryTool(IMemoryService memoryService, AgentEvents events) : IAgentTool
{
    public AIFunction AsAiFunction() => AIFunctionFactory.Create(
        (string memory) => SaveAsync(memory),
        "save_memory",
        "Saves a fact to persistent memory so it's available in future sessions. Use for discovered environment facts like tool versions, installed modules, user preferences, and common paths. Do not save conversation-specific context.");

    private async Task<string> SaveAsync(string memory)
    {
        events.RaiseToolActivity("New memory: " + memory);
        return await memoryService.SaveMemoryAsync(memory);
    }
}

public class DeleteMemoryTool(IMemoryService memoryService, AgentEvents events) : IAgentTool
{
    public AIFunction AsAiFunction() => AIFunctionFactory.Create(
        (int index) => DeleteAsync(index),
        "delete_memory",
        "Deletes a memory by its 1-based index number. Use list_memory first to find the index.");

    private async Task<string> DeleteAsync(int index)
    {
        events.RaiseToolActivity("Forget memory #" + index);
        return await memoryService.DeleteMemoryAsync(index);
    }
}

public class UpdateMemoryTool(IMemoryService memoryService, AgentEvents events) : IAgentTool
{
    public AIFunction AsAiFunction() => AIFunctionFactory.Create(
        (int index, string memory) => UpdateAsync(index, memory),
        "update_memory",
        "Replaces an existing memory at the given 1-based index with new text. Use when a saved fact is outdated or inaccurate.");

    private async Task<string> UpdateAsync(int index, string memory)
    {
        events.RaiseToolActivity("Update memory #" + index);
        return await memoryService.UpdateMemoryAsync(index, memory);
    }
}

public class ListMemoryTool(IMemoryService memoryService, AgentEvents events) : IAgentTool
{
    public AIFunction AsAiFunction() => AIFunctionFactory.Create(
        () => ListAsync(),
        "list_memory",
        "Returns all saved memories as a numbered list. Check this before running environment discovery commands.");

    private async Task<string> ListAsync()
    {
        events.RaiseToolActivity("List memories");
        return await memoryService.ListMemoriesAsync();
    }
}

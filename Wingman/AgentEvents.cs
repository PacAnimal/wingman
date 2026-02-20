namespace Wingman;

/// <summary>
/// Thin event bus for cross-cutting agent lifecycle signals.
/// Tools fire RaiseToolStarted() so ChatPanel can open a fresh bubble for the post-tool response.
/// </summary>
public sealed class AgentEvents
{
    // fired from background threads — subscribers must be thread-safe
    public event Action? ToolStarted;

    internal void RaiseToolStarted() => ToolStarted?.Invoke();
}

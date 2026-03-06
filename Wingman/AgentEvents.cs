namespace Wingman;

/// <summary>
/// Thin event bus for cross-cutting agent lifecycle signals.
/// Tools fire RaiseToolStarted() so ChatPanel can open a fresh bubble for the post-tool response.
/// </summary>
public sealed class AgentEvents
{
    // fired from background threads — subscribers must be thread-safe
    public event Action? ToolStarted;
    public event Action<string>? ToolActivity;
    public event Action<string>? ToolResultLogged;
    public event Action? ThinkingStarted;
    public event Action? ThinkingStopped;
    public event Action? CommandStarting;
    public event Action? CardWaitStarted;
    public event Action? CardWaitEnded;

    internal void RaiseToolStarted() => ToolStarted?.Invoke();
    internal void RaiseToolActivity(string message) { ToolStarted?.Invoke(); ToolActivity?.Invoke(message); }
    internal void RaiseToolResult(string summary) => ToolResultLogged?.Invoke(summary);
    internal void RaiseThinkingStarted() => ThinkingStarted?.Invoke();
    internal void RaiseThinkingStopped() => ThinkingStopped?.Invoke();
    internal void RaiseCommandStarting() => CommandStarting?.Invoke();
    internal void RaiseCardWaitStarted() => CardWaitStarted?.Invoke();
    internal void RaiseCardWaitEnded() => CardWaitEnded?.Invoke();
}

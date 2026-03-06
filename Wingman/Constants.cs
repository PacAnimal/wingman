namespace Wingman;

internal static class Constants
{
    internal static readonly int CommandTimeoutMs = (int)TimeSpan.FromHours(24).TotalMilliseconds;
    internal static readonly int GuardTimeoutMs = (int)TimeSpan.FromSeconds(30).TotalMilliseconds;

    internal const int SpinnerCommandFrameMs = 100;
    internal const int SpinnerThinkingMinFrameMs = 50;
    internal const int SpinnerThinkingMaxFrameMs = 250;
    internal const int SpinnerThinkingMinSpeedSwitchIntervalMs = 250;
    internal const int SpinnerThinkingMaxSpeedSwitchIntervalMs = 1500;
    internal const int TaskTypingIntervalMinutes = 5;
    internal const int TaskFirstCommandDelaySeconds = 5;

    internal const int MaxMemories = 100;

    // when history exceeds this, summarize old messages before sending to the API
    internal const int ContextSummarizeThreshold = 100;
    // keep last N messages verbatim; everything older gets summarized
    internal const int ContextRecentToKeep = 80;

    // user terminal command summarization
    internal const int UserCommandSummarizeThreshold = 20;
    internal const int UserCommandSummarizeMaxInputChars = 16_000;
    internal const int UserCommandFallbackMaxChars = 4_000;
}

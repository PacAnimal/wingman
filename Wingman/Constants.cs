namespace Wingman;

internal static class Constants
{
    internal static readonly int CommandTimeoutMs = (int)TimeSpan.FromHours(24).TotalMilliseconds;
    internal static readonly int GuardTimeoutMs = (int)TimeSpan.FromSeconds(30).TotalMilliseconds;

    internal const string ChatModel = "gpt-5.2";
    internal const string GuardModel = "gpt-5-mini";

    internal const int SpinnerCommandFrameMs = 100;
    internal const int SpinnerThinkingMinFrameMs = 50;
    internal const int SpinnerThinkingMaxFrameMs = 250;
    internal const int SpinnerThinkingMinSpeedSwitchIntervalMs = 250;
    internal const int SpinnerThinkingMaxSpeedSwitchIntervalMs = 1500;
    internal const int TaskTypingIntervalMinutes = 5;
    internal const int TaskFirstCommandDelaySeconds = 60;
}

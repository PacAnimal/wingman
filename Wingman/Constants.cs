namespace Wingman;

internal static class Constants
{
    internal static readonly int CommandTimeoutMs = (int)TimeSpan.FromHours(24).TotalMilliseconds;
    internal static readonly int GuardTimeoutMs = (int)TimeSpan.FromSeconds(30).TotalMilliseconds;

    internal const string ChatModel = "gpt-5.2";
    internal const string GuardModel = "gpt-5-mini";

    internal const int SpinnerCommandIntervalMs = 200;
    internal const int SpinnerThinkingMaxIntervalMs = 500;
    internal const int TaskTypingIntervalMinutes = 5;
    internal const int TaskFirstCommandDelaySeconds = 60;
}

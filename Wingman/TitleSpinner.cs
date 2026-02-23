using System.Windows.Threading;

namespace Wingman;

internal sealed class TitleSpinner : IDisposable
{
    private enum SpinMode { Idle, Command, Thinking }

    private static readonly char[] Frames = ['⠋', '⠙', '⠹', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];

    private readonly DispatcherTimer _timer;
    private readonly Action<char?> _callback;
    private SpinMode _mode = SpinMode.Idle;
    private int _frameIndex;
    private DateTime _burstEnd;
    private bool _disposed;

    public TitleSpinner(Action<char?> callback)
    {
        _callback = callback;
        _timer = new DispatcherTimer();
        _timer.Tick += OnTick;
    }

    public char? CurrentFrame => _mode == SpinMode.Idle ? null : Frames[_frameIndex];

    public void StartCommand()
    {
        _mode = SpinMode.Command;
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(Constants.SpinnerCommandFrameMs);
        _timer.Start();
        _callback(CurrentFrame);
    }

    public void StartThinking()
    {
        _mode = SpinMode.Thinking;
        _timer.Stop();
        StartNewBurst();
        _callback(CurrentFrame);
    }

    public void Stop()
    {
        _mode = SpinMode.Idle;
        _timer.Stop();
        _callback(null);
    }

    private void StartNewBurst()
    {
        var burstMs = Random.Shared.Next(Constants.SpinnerThinkingMinSpeedSwitchIntervalMs, Constants.SpinnerThinkingMaxSpeedSwitchIntervalMs + 1);
        var intervalMs = Random.Shared.Next(Constants.SpinnerThinkingMinFrameMs, Constants.SpinnerThinkingMaxFrameMs + 1);
        _burstEnd = DateTime.UtcNow.AddMilliseconds(burstMs);
        _timer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _frameIndex = (_frameIndex + 1) % Frames.Length;
        if (_mode == SpinMode.Thinking && DateTime.UtcNow >= _burstEnd)
        {
            _timer.Stop();
            StartNewBurst();
        }
        _callback(CurrentFrame);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Automation;
using Cathedral.Extensions;
using Cathedral.Utils;
using EasyWindowsTerminalControl;
using Microsoft.Extensions.Logging;

namespace Wingman;

// ReSharper disable NotAccessedPositionalProperty.Global
public record CommandResult(string Command, string Output, int ExitCode, bool Success, string WorkingDirectory, bool Truncated, string Duration);
// ReSharper restore NotAccessedPositionalProperty.Global

public interface ITerminal
{
    Task Init(EasyTerminalControl terminalControl);
    Task Reset();
    Task<CommandResult> RunCommand(string command);
    bool IsCommandRunning { get; }
    void SendCtrlC();
    string ScratchDir { get; }
    event Action? ProcessExited;
    event Action? CommandCompleted;
}

public class Terminal(ILogger<Terminal> log, IScreenBuffer screenBuffer) : ITerminal
{
    private const int MaxOutputLength = 65_536;

    private readonly SemaphoreSlimValue<StringBuilder> _outputBuffer = new(new StringBuilder(), disposeValue: false);
    private readonly Channel<bool> _sentinels = Channel.CreateUnbounded<bool>();
    private readonly SemaphoreSlim _commandLock = new(0, 1);
    private TaskCompletionSource _termStarted = new();
    private TermPTY? _term;
    private string? _scratchDir;
    private EasyTerminalControl? _terminalControl;
    private int _generation;
    private CancellationTokenSource? _resetCts;
    private volatile bool _commandRunning;

    public string ScratchDir => _scratchDir ?? throw new InvalidOperationException("Terminal not ready");
    public bool IsCommandRunning => _commandRunning;
    public void SendCtrlC() => _term?.WriteToTerm("\x03");

    public event Action? ProcessExited;
    public event Action? CommandCompleted;

    private string Sentinel { get; } = $"{Guid.NewGuid()}";
    private string FormattedSentinel => $"[{Sentinel}]";

    public async Task Init(EasyTerminalControl terminalControl)
    {
        _terminalControl = terminalControl;
        var conPty = terminalControl.DisconnectConPTYTerm();
        ++_generation;

        // initial viewport size matches the conPty.Start() call below
        screenBuffer.Resize(24, 80);
        // debounce: wait 300ms after last resize, then wipe + repopulate from UIA
        var resizeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        resizeTimer.Tick += (_, _) =>
        {
            resizeTimer.Stop();
            var t = terminalControl.Terminal;
            screenBuffer.Resize(t?.Rows ?? 0, t?.Columns ?? 0);
            RefreshScreenBufferFromUia();
        };
        terminalControl.SizeChanged += (_, _) =>
        {
            resizeTimer.Stop();
            resizeTimer.Start();
        };

        await InitCore(conPty);
    }

    public async Task Reset()
    {
        _resetCts?.Cancel();

        // wait for any in-flight RunCommand to release the lock
        await _commandLock.WaitAsync();

        ++_generation;
        _termStarted = new TaskCompletionSource();
        while (_sentinels.Reader.TryRead(out _)) { }
        screenBuffer.Reset();

        using (var buf = await _outputBuffer.WaitForDisposable())
            buf.Value.Clear();

        if (_scratchDir != null)
        {
            try { Directory.Delete(_scratchDir, recursive: true); } catch { /* best-effort */ }
            _scratchDir = null;
        }

        var oldTerm = _term;
        _term = null;

        // detach old TermPTY from UI (we're on the UI thread via WPF sync context)
        _terminalControl!.DisconnectConPTYTerm();

        try { oldTerm?.CloseStdinToApp(); } catch { /* best-effort */ }
        try { oldTerm?.StopExternalTermOnly(); } catch { /* best-effort */ }

        _resetCts?.Dispose();
        _resetCts = null;

        await InitCore(new TermPTY());
    }

    private async Task InitCore(TermPTY conPty)
    {
        var gen = _generation;

        var hwndReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // HWND exists once Terminal.Loaded fires; on reset it's already loaded
        if (_terminalControl!.Terminal.IsLoaded)
            hwndReady.TrySetResult();
        else
            _terminalControl.Terminal.Loaded += (_, _) => hwndReady.TrySetResult();

        // set interceptor BEFORE start so no output is missed
        conPty.InterceptOutputToUITerminal = (ref str) =>
        {
            if (_generation != gen) return;
            var raw = str.ToString();
            screenBuffer.Feed(raw);           // raw ANSI for cursor tracking
            var stripped = TermPTY.StripColors(raw);
            _termStarted.TrySetResult();
            using (var buf = _outputBuffer.WaitForDisposable().GetAwaiter().GetResult())
                buf.Value.Append(stripped);
            var pos = 0;
            while ((pos = stripped.IndexOf(FormattedSentinel, pos, StringComparison.Ordinal)) >= 0)
            {
                _sentinels.Writer.TryWrite(true);
                pos += FormattedSentinel.Length;
            }
        };

        conPty.TermReady += (_, _) =>
        {
            if (_generation != gen) return;
            _term = conPty;

            // block until HWND is ready — Start() can't reach ReadOutputLoop() until we return
            hwndReady.Task.Wait();

            // re-attach to display
            _terminalControl.Dispatcher.Invoke(() => _terminalControl.ConPTYTerm = conPty);

            // only fire ProcessExited for the current generation's process
            _ = Task.Run(() => { conPty.Process?.WaitForExit(); if (_generation == gen) ProcessExited?.Invoke(); });

            _ = Task.Run(async () =>
            {
                if (_generation != gen) return;
                await _termStarted.Task;

                var scratchDir = Path.Combine(Path.GetTempPath(), "Wingman", Guid.NewGuid().ToString());
                Directory.CreateDirectory(scratchDir);
                _scratchDir = scratchDir;

                var sentinelLeft = FormattedSentinel[..(FormattedSentinel.Length / 2)];
                var sentinelRight = FormattedSentinel[(FormattedSentinel.Length / 2)..];
                WriteCommand($$"""
                               $WINGMAN_SENTINEL_LEFT = "{{sentinelLeft}}"
                               $WINGMAN_SENTINEL_RIGHT = "{{sentinelRight}}"
                               $env:WMTMP = "{{scratchDir}}"
                               New-Variable -Name WMTMP -Value "{{scratchDir}}" -Option Constant -Scope Global
                               Set-PSReadLineOption -HistorySaveStyle SaveNothing
                               function prompt {
                                   $wm_ok = $?
                                   $wm_code = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }
                                   $wm_cwd = $PWD.Path
                                   $wm_s = "${WINGMAN_SENTINEL_LEFT}${WINGMAN_SENTINEL_RIGHT}"
                                   $wm_exit = "${wm_code}|${wm_ok}"
                                   function wm_hide($t) { Write-Host "`e[30m${t}`e[0m`r$(' ' * $t.Length)`r" -NoNewline }
                                   wm_hide $wm_s
                                   wm_hide $wm_exit
                                   wm_hide $wm_s
                                   wm_hide $wm_cwd
                                   wm_hide $wm_s
                                   "PS $($executionContext.SessionState.Path.CurrentLocation)> "
                               }
                               [Microsoft.PowerShell.PSConsoleReadLine]::ClearHistory(); clear; Write-Host "`nWingman ready!`n" -ForegroundColor Green
                               """);

                // drain init sentinels - prompt fires twice during init, 3 sentinels each
                await WaitForSentinel(6);

                if (_generation != gen) return;
                _resetCts = new CancellationTokenSource();
                initComplete.TrySetResult();
            });
        };

        _ = Task.Run(() => conPty.Start("pwsh.exe -NoProfile", 80, 24, factory: new DetachedProcessFactory()));

        await initComplete.Task;

        // let commands run
        _commandLock.Release();
    }

    public async Task<CommandResult> RunCommand(string command)
    {
        if (_term is null) throw new InvalidOperationException("Terminal not ready");

        using var _ = await _commandLock.WaitForDisposable();
        _commandRunning = true;
        try
        {
            // gobble stale sentinels from user interaction or background prompt renders
            var drained = 0;
            while (_sentinels.Reader.TryRead(out var stale)) drained++;
            if (drained > 0) log.LogDebug("Drained {Count} stale sentinel(s)", drained);

            int offset;
            using (var buf = await _outputBuffer.WaitForDisposable())
                offset = buf.Value.Length;

            var sw = Stopwatch.StartNew();
            WriteCommand(command);

            // wait until all 3 sentinels appear in buffer after offset; cancellable via reset or timeout
            using var timeout = new CancellationTokenSource(Constants.CommandTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _resetCts!.Token);
            var sentinelLen = FormattedSentinel.Length;

            while (true)
            {
                await _sentinels.Reader.ReadAsync(linked.Token);

                string buffer;
                using (var buf = await _outputBuffer.WaitForDisposable(linked.Token))
                    buffer = buf.Value.ToString();

                var s1 = buffer.IndexOf(FormattedSentinel, offset, StringComparison.Ordinal);
                if (s1 < 0) continue;
                var s2 = buffer.IndexOf(FormattedSentinel, s1 + sentinelLen, StringComparison.Ordinal);
                if (s2 < 0) continue;
                var s3 = buffer.IndexOf(FormattedSentinel, s2 + sentinelLen, StringComparison.Ordinal);
                if (s3 < 0) continue;

                var output = buffer[offset..s1];
                var exitRaw = buffer[(s1 + sentinelLen)..s2];
                var cwdRaw = buffer[(s2 + sentinelLen)..s3];

                // trim consumed portion to prevent indefinite growth
                using (var buf = await _outputBuffer.WaitForDisposable(linked.Token))
                    buf.Value.Remove(0, s3 + sentinelLen);

                // strip the echoed command (first line)
                var firstNewline = output.IndexOf('\n');
                if (firstNewline >= 0) output = output[(firstNewline + 1)..];

                // parse exit status: "0|True" or "1|False"
                var exitParts = ExtractHiddenData(exitRaw).Split('|');
                var exitCode = int.TryParse(exitParts.Length > 0 ? exitParts[0] : null, out var parsedCode) ? parsedCode : 0;
                var success = !bool.TryParse(exitParts.Length > 1 ? exitParts[1] : null, out var parsedSuccess) || parsedSuccess;
                var cwd = ExtractHiddenData(cwdRaw);

                var trimmed = output.Trim();
                var truncated = trimmed.Length > MaxOutputLength;
                if (truncated) trimmed = trimmed[..MaxOutputLength];
                var result = new CommandResult(command, trimmed, exitCode, success, cwd, truncated, sw.Elapsed.ToString(@"hh\:mm\:ss"));
                log.LogInformation("Command executed in {Elapsed}ms: {Command}", (int)sw.ElapsedMilliseconds, command);
                log.LogDebug("Command result: {Result}", JsonSerializer.Serialize(result));
                CommandCompleted?.Invoke();
                return result;
            }
        }
        finally
        {
            _commandRunning = false;
        }
    }

    private static string ExtractHiddenData(string raw)
    {
        foreach (var part in raw.Split('\r'))
            if (part.Length > 0 && !part.AsSpan().IsWhiteSpace())
                return part;
        return "";
    }

    private async Task WaitForSentinel(int count = 1, int timeoutMs = 30000)
    {
        for (var i = count; i > 0; i--)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await _sentinels.Reader.ReadAsync(cts.Token);
        }
    }

    private void WriteCommand(string command)
    {
        // clear any partially-typed user input before injecting
        _term!.WriteToTerm(new string('\x08', 2048));
        foreach (var c in command.SaneSplit('\r', '\n'))
        {
            _term!.WriteToTerm(c + '\r');
        }
    }

    // reflect into EasyWindowsTerminalControl internals to get the native terminal HWND
    private IntPtr TryGetTerminalHwnd()
    {
        var terminal = _terminalControl?.Terminal;
        if (terminal is null) return IntPtr.Zero;
        // termContainer is an x:Name XAML field on TerminalControl (private in generated code)
        var field = terminal.GetType().GetField("termContainer", BindingFlags.NonPublic | BindingFlags.Instance);
        var container = field?.GetValue(terminal);
        if (container is null) return IntPtr.Zero;
        // TerminalContainer.Hwnd is internal — the native HWND created by NativeMethods.CreateTerminal()
        var prop = container.GetType().GetProperty("Hwnd", BindingFlags.NonPublic | BindingFlags.Instance);
        return prop?.GetValue(container) is IntPtr hwnd ? hwnd : IntPtr.Zero;
    }

    // read the visible viewport text via UIA and push it into the screen buffer
    private void RefreshScreenBufferFromUia()
    {
        var hwnd = TryGetTerminalHwnd();
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            if (element.GetCurrentPattern(TextPattern.Pattern) is not TextPattern textPattern) return;
            var ranges = textPattern.GetVisibleRanges();
            if (ranges.Length == 0) return;
            var sb = new StringBuilder();
            for (var i = 0; i < ranges.Length; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(ranges[i].GetText(-1).TrimEnd('\n', '\r'));
            }
            screenBuffer.FillFromText(sb.ToString());
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "UIA screen refresh failed");
        }
    }
}

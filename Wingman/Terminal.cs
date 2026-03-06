using System.Collections.Concurrent;
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
public record UserCommandInfo(string Command, string Output, int ExitCode, bool Success, string WorkingDirectory);
// ReSharper restore NotAccessedPositionalProperty.Global

public interface ITerminal
{
    Task Init(EasyTerminalControl terminalControl, int cols = 80, int rows = 24);
    Task Reset();
    Task<CommandResult> RunCommand(string command);
    bool IsCommandRunning { get; }
    void SendCtrlC();
    string ScratchDir { get; }
    event Action? ProcessExited;
    event Action? CommandCompleted;
    event Action? UserCommandDetected;
    List<UserCommandInfo> DrainUserCommands();
}

public class Terminal(ILogger<Terminal> log, IScreenBuffer screenBuffer) : ITerminal, IDisposable
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
    private volatile bool _initDone;
    private int _userCommandOffset;
    private string _lastHistoryCommand = "";
    private readonly ConcurrentQueue<UserCommandInfo> _pendingUserCommands = new();

    public string ScratchDir => _scratchDir ?? throw new InvalidOperationException("Terminal not ready");
    public bool IsCommandRunning => _commandRunning;
    public void SendCtrlC() => _term?.WriteToTerm("\x03");

    private bool _disposed;

    public event Action? ProcessExited;
    public event Action? CommandCompleted;
    public event Action? UserCommandDetected;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);

        _resetCts?.Cancel();
        _resetCts?.Dispose();

        try { _term?.CloseStdinToApp(); } catch { /* best-effort */ }
        try { _term?.StopExternalTermOnly(); } catch { /* best-effort */ }

        if (_scratchDir != null)
        {
            try { Directory.Delete(_scratchDir, recursive: true); } catch { /* best-effort */ }
            _scratchDir = null;
        }
    }

    private string Sentinel { get; } = $"{Guid.NewGuid()}";
    private string FormattedSentinel => $"[{Sentinel}]";

    public async Task Init(EasyTerminalControl terminalControl, int cols = 80, int rows = 24)
    {
        _terminalControl = terminalControl;
        var conPty = terminalControl.DisconnectConPTYTerm();
        ++_generation;

        // initial viewport size matches the conPty.Start() call below
        screenBuffer.Resize(rows, cols);
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

        await InitCore(conPty, cols, rows);
    }

    public async Task Reset()
    {
        _resetCts?.Cancel();

        // wait for any in-flight RunCommand to release the lock
        await _commandLock.WaitAsync();

        ++_generation;
        _termStarted = new TaskCompletionSource();
        while (_sentinels.Reader.TryRead(out _)) { }
        while (_pendingUserCommands.TryDequeue(out _)) { }
        _lastHistoryCommand = "";
        screenBuffer.Reset();

        using (var buf = await _outputBuffer.WaitForDisposable())
        {
            buf.Value.Clear();
            _userCommandOffset = 0;
        }

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

        var t = _terminalControl!.Terminal;
        await InitCore(new TermPTY(), t?.Columns ?? 80, t?.Rows ?? 24);
    }

    private async Task InitCore(TermPTY conPty, int cols, int rows)
    {
        _initDone = false;
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
                if (_initDone && !_commandRunning)
                    UserCommandDetected?.Invoke();
                pos += FormattedSentinel.Length;
            }
        };

        conPty.TermReady += (_, _) =>
        {
            if (_generation != gen) return;
            _term = conPty;

            // block until HWND is ready — Start() can't reach ReadOutputLoop() until we return
            hwndReady.Task.Wait();

            // attach to display; Term_TermReady fires synchronously and calls ConPTYTerm.Resize(Terminal.Columns, Terminal.Rows)
            // but for hidden tabs WM_WINDOWPOSCHANGED hasn't fired yet so Terminal.Columns=0 — restore correct dimensions
            _terminalControl!.Dispatcher.Invoke(() =>
            {
                _terminalControl.ConPTYTerm = conPty;
                var c = _terminalControl.Terminal?.Columns ?? 0;
                var r = _terminalControl.Terminal?.Rows ?? 0;
                if (c <= 0 || r <= 0)
                    conPty.Resize(cols, rows);
            });

            // only fire ProcessExited for the current generation's process
            _ = Task.Run(() => { conPty.Process?.WaitForExit(); if (_generation == gen) ProcessExited?.Invoke(); });

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_generation != gen) { initComplete.TrySetCanceled(); return; }
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
                                       $wm_cmd = if ($h = Get-History -Count 1) { $h.CommandLine } else { '' }
                                       $wm_cmd = ($wm_cmd -replace '[\r\n]+', ' ')
                                       $wm_s = "${WINGMAN_SENTINEL_LEFT}${WINGMAN_SENTINEL_RIGHT}"
                                       $wm_exit = "${wm_code}|${wm_ok}"
                                       function wm_hide($t) { Write-Host "`e[30m${t}`e[0m`r$(' ' * $t.Length)`r" -NoNewline }
                                       wm_hide $wm_s
                                       wm_hide $wm_exit
                                       wm_hide $wm_s
                                       wm_hide $wm_cwd
                                       wm_hide $wm_s
                                       wm_hide $wm_cmd
                                       wm_hide $wm_s
                                       "PS $($executionContext.SessionState.Path.CurrentLocation)> "
                                   }
                                   [Microsoft.PowerShell.PSConsoleReadLine]::ClearHistory()
                                   """);

                    // drain init sentinels - prompt fires twice during init, 4 sentinels each
                    await WaitForSentinel(8);

                    if (_generation != gen) { initComplete.TrySetCanceled(); return; }
                    // skip init output — user commands start from here
                    using (var buf = await _outputBuffer.WaitForDisposable())
                        _userCommandOffset = buf.Value.Length;

                    _resetCts = new CancellationTokenSource();
                    initComplete.TrySetResult();
                }
                catch (Exception ex)
                {
                    initComplete.TrySetException(ex);
                }
            });
        };

        _ = Task.Run(() => conPty.Start("pwsh.exe -NoProfile", cols, rows, factory: new DetachedProcessFactory()));

        await initComplete.Task;

        // let commands run
        _commandLock.Release();
        _initDone = true;
    }

    public async Task<CommandResult> RunCommand(string command)
    {
        if (_term is null) throw new InvalidOperationException("Terminal not ready");

        using var _ = await _commandLock.WaitForDisposable();
        _commandRunning = true;
        try
        {
            int offset;
            using (var buf = await _outputBuffer.WaitForDisposable())
            {
                // capture any user commands from stale buffer content before recording offset
                ParseUserCommands(buf.Value);
                offset = buf.Value.Length;
            }

            // drain stale sentinel signals — already processed as user commands above
            var drained = 0;
            while (_sentinels.Reader.TryRead(out bool stale)) drained++;
            if (drained > 0) log.LogDebug("Drained {Count} stale sentinel(s)", drained);

            var sw = Stopwatch.StartNew();
            WriteCommand(command);

            // wait until all 4 sentinels appear in buffer after offset; cancellable via reset or timeout
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
                var s4 = buffer.IndexOf(FormattedSentinel, s3 + sentinelLen, StringComparison.Ordinal);
                if (s4 < 0) continue;

                var output = buffer[offset..s1];
                var exitRaw = buffer[(s1 + sentinelLen)..s2];
                var cwdRaw = buffer[(s2 + sentinelLen)..s3];
                // s3-s4 holds the command echo; ignored here since RunCommand knows its own command

                // trim consumed portion to prevent indefinite growth
                using (var buf = await _outputBuffer.WaitForDisposable(linked.Token))
                {
                    buf.Value.Remove(0, s4 + sentinelLen);
                    _userCommandOffset = Math.Max(0, _userCommandOffset - (s4 + sentinelLen));
                }

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

    // scan buffer for complete sentinel groups since _userCommandOffset; must be called under _outputBuffer semaphore
    private void ParseUserCommands(StringBuilder buf)
    {
        var sentinel = FormattedSentinel;
        var sentinelLen = sentinel.Length;
        var text = buf.ToString();
        var pos = _userCommandOffset;

        while (true)
        {
            var s1 = text.IndexOf(sentinel, pos, StringComparison.Ordinal);
            if (s1 < 0) break;
            var s2 = text.IndexOf(sentinel, s1 + sentinelLen, StringComparison.Ordinal);
            if (s2 < 0) break;
            var s3 = text.IndexOf(sentinel, s2 + sentinelLen, StringComparison.Ordinal);
            if (s3 < 0) break;
            var s4 = text.IndexOf(sentinel, s3 + sentinelLen, StringComparison.Ordinal);
            if (s4 < 0) break;

            var region = text[pos..s1];
            var exitRaw = text[(s1 + sentinelLen)..s2];
            var cwdRaw = text[(s2 + sentinelLen)..s3];
            var cmdRaw = text[(s3 + sentinelLen)..s4];

            // strip echoed command (first line) from output
            var nl = region.IndexOf('\n');
            var output = nl >= 0 ? region[(nl + 1)..] : "";

            var command = ExtractHiddenData(cmdRaw);

            var exitParts = ExtractHiddenData(exitRaw).Split('|');
            var exitCode = int.TryParse(exitParts.Length > 0 ? exitParts[0] : null, out var parsedCode) ? parsedCode : 0;
            var success = !bool.TryParse(exitParts.Length > 1 ? exitParts[1] : null, out var parsedSuccess) || parsedSuccess;
            var cwd = ExtractHiddenData(cwdRaw);

            pos = s4 + sentinelLen;

            // skip empty-enter repeats (Get-History returns last command when nothing was typed)
            if (!string.IsNullOrWhiteSpace(command) && command != _lastHistoryCommand)
            {
                _lastHistoryCommand = command;
                var trimmedOutput = output.Trim();
                if (trimmedOutput.Length > MaxOutputLength) trimmedOutput = trimmedOutput[..MaxOutputLength];
                _pendingUserCommands.Enqueue(new UserCommandInfo(command, trimmedOutput, exitCode, success, cwd));
                UserCommandDetected?.Invoke();
            }
        }

        _userCommandOffset = pos;
    }

    public List<UserCommandInfo> DrainUserCommands()
    {
        using var buf = _outputBuffer.WaitForDisposable().GetAwaiter().GetResult();
        ParseUserCommands(buf.Value);

        // trim buffer if no command is running — safe because _commandRunning is set before RunCommand acquires this semaphore
        if (!_commandRunning && _userCommandOffset > 0)
        {
            buf.Value.Remove(0, _userCommandOffset);
            _userCommandOffset = 0;
        }

        var result = new List<UserCommandInfo>();
        while (_pendingUserCommands.TryDequeue(out var cmd))
            result.Add(cmd);
        return result;
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

using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Cathedral.Extensions;
using EasyWindowsTerminalControl;
using Microsoft.Extensions.Logging;

namespace Wingman;

public interface ITerminal
{
    Task Init(EasyTerminalControl terminalControl);
    Task<string> RunCommand(string command, int timeoutMs = 30000);
    event Action? ProcessExited;
}

public class Terminal(ILogger<Terminal> log) : ITerminal
{
    private readonly StringBuilder _outputBuffer = new();
    private readonly Channel<bool> _sentinels = Channel.CreateUnbounded<bool>();
    private readonly TaskCompletionSource _termStarted = new();
    private readonly SemaphoreSlim _commandLock = new(0, 1);
    private TermPTY? _term;

    public event Action? ProcessExited;

    private string Sentinel { get; } = $"{Guid.NewGuid()}";
    private string FormattedSentinel => $"[{Sentinel}]";

    public async Task Init(EasyTerminalControl terminalControl)
    {
        var conPty = terminalControl.DisconnectConPTYTerm();

        // signal: native HWND exists (BuildWindowCore has run before Loaded fires)
        var hwndReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        terminalControl.Terminal.Loaded += (_, _) => hwndReady.TrySetResult();

        // set interceptor BEFORE start so no output is missed
        conPty.InterceptOutputToUITerminal = (ref str) =>
        {
            var text = TermPTY.StripColors(str.ToString());
            _termStarted.TrySetResult();
            _outputBuffer.Append(text);
            var pos = 0;
            while ((pos = text.IndexOf(FormattedSentinel, pos, StringComparison.Ordinal)) >= 0)
            {
                _sentinels.Writer.TryWrite(true);
                pos += FormattedSentinel.Length;
            }
        };

        conPty.TermReady += (_, _) =>
        {
            _term = conPty;

            // block until HWND is ready — Start() can't reach ReadOutputLoop() until we return
            hwndReady.Task.Wait();

            // re-attach to display: OnTermChanged sees TermProcIsStarted=true → calls Term_TermReady
            // immediately (inline on UI thread) → sets Terminal.Connection before ReadOutputLoop reads
            terminalControl.Dispatcher.Invoke(() => terminalControl.ConPTYTerm = conPty);

            _ = Task.Run(() => { conPty.Process?.WaitForExit(); ProcessExited?.Invoke(); });
            _ = Task.Run(async () =>
            {
                await _termStarted.Task;
                
                // store sentinel in two ps variables so the literal guid never appears in a command
                var spaces = new string(' ', FormattedSentinel.Length);
                var sentinelLeft = FormattedSentinel[..(FormattedSentinel.Length/2)];
                var sentinelRight = FormattedSentinel[(FormattedSentinel.Length/2)..];
                WriteCommand($$"""
                               $WINGMAN_SENTINEL_LEFT = "{{sentinelLeft}}"
                               $WINGMAN_SENTINEL_RIGHT = "{{sentinelRight}}"
                               Set-PSReadLineOption -HistorySaveStyle SaveNothing
                               function prompt { Write-Host "`e[30m${WINGMAN_SENTINEL_LEFT}${WINGMAN_SENTINEL_RIGHT}`e[0m`r{{spaces}}`r" -NoNewline; "PS $($executionContext.SessionState.Path.CurrentLocation)> " }
                               [Microsoft.PowerShell.PSConsoleReadLine]::ClearHistory(); clear; Write-Host "`nWingman ready!`n" -ForegroundColor Green
                               """);

                // drain init sentinels - the "prompt" command and the Write-Host command print one each
                await WaitForSentinel(2);
                initComplete.TrySetResult();
            });
        };

        // sole caller of Start — no race, no try-catch needed
        _ = Task.Run(() => conPty.Start("pwsh.exe -NoProfile", 80, 24, factory: new DetachedProcessFactory()));

        // wait for initialization to complete
        await initComplete.Task;
        
        // let commands run
        _commandLock.Release();
    }

    public async Task<string> RunCommand(string command, int timeoutMs = 30000)
    {
        if (_term is null) throw new InvalidOperationException("Terminal not ready");

        using var _ = await _commandLock.WaitForDisposable();
        var offset = _outputBuffer.Length;
        var sw = Stopwatch.StartNew();
        WriteCommand(command);

        // wait for sentinel from prompt after command finishes
        await WaitForSentinel(timeoutMs: timeoutMs);
        var endIdx = _outputBuffer.ToString().LastIndexOfOrdinal(FormattedSentinel);

        var output = _outputBuffer.ToString()[offset..endIdx];

        // strip the echoed command (first line)
        var firstNewline = output.IndexOf('\n');
        if (firstNewline >= 0)
            output = output[(firstNewline + 1)..];

        var elapsed = (int)sw.ElapsedMilliseconds;
        log.LogInformation("Command executed in {Elapsed}ms: {Command}", elapsed, command);

        return output.Trim();
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
        foreach (var c in command.SaneSplit('\r', '\n'))
        {
            _term!.WriteToTerm(c + '\r');
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Wingman;

public partial class MainWindow : Window
{
    private readonly StringBuilder _outputBuffer = new();
    private readonly Channel<bool> _sentinels = Channel.CreateUnbounded<bool>();
    private readonly TaskCompletionSource _termStarted = new();
    private readonly string _sentinel;
    private EasyWindowsTerminalControl.TermPTY? _term;

    public MainWindow()
    {
        InitializeComponent();
        // delimiters only added by powershell at output time — raw guid in variable assignment
        var rawGuid = Guid.NewGuid().ToString();
        _sentinel = $"<<{rawGuid}>>";
        ProbeNativeDeps();

        // intercept ctrl+c before WM_KEYDOWN reaches the native terminal hwnd — selection is still
        // active at this point; by the time InterceptInputToTermApp fires it's already cleared
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
        Closed += (_, _) => ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;

        Terminal.ConPTYTerm.TermReady += (sender, _) =>
        {
            if (sender is not EasyWindowsTerminalControl.TermPTY term) return;
            _term = term;

            term.InterceptOutputToUITerminal = (ref Span<char> str) =>
            {
                var text = EasyWindowsTerminalControl.TermPTY.StripColors(str.ToString());
                _termStarted.TrySetResult();
                _outputBuffer.Append(text);
                var pos = 0;
                while ((pos = text.IndexOf(_sentinel, pos, StringComparison.Ordinal)) >= 0)
                {
                    _sentinels.Writer.TryWrite(true);
                    pos += _sentinel.Length;
                }
            };

            Task.Run(() => { term.Process?.WaitForExit(); Dispatcher.BeginInvoke(Close); });

            Task.Run(async () =>
            {
                await _termStarted.Task;
                // store sentinel in a ps variable so the literal guid never appears in the command
                // text — prevents the PSReadLine echo from triggering our intercept early
                var spaces = new string(' ', _sentinel.Length);
                term.WriteToTerm("Set-PSReadLineOption -HistorySaveStyle SaveNothing\r");
                term.WriteToTerm($"$wm_sentinel='{rawGuid}'\r");
                term.WriteToTerm(
                    $"function prompt {{ Write-Host \"`e[30m<<$wm_sentinel>>`e[0m`r{spaces}`r\" -NoNewline; " +
                    "\"PS $($executionContext.SessionState.Path.CurrentLocation)> \" }\r");
                term.WriteToTerm("[Microsoft.PowerShell.PSConsoleReadLine]::ClearHistory(); clear; Write-Host \"`nWingman ready!`n\" -ForegroundColor Green\r");

                var result = await RunCommand("dir");
                File.WriteAllText(@"C:\temp\wingman.log", result);
            });
        };
    }

    private void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        const int VK_C = 0x43;
        if (msg.message != WM_KEYDOWN || (int)msg.wParam != VK_C) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        var selected = Terminal.Terminal.GetSelectedText();
        if (string.IsNullOrEmpty(selected)) return;

        Clipboard.SetText(selected);
        handled = true; // suppress ^C — don't let it reach the terminal
    }

    private async Task<string> RunCommand(string command, int timeoutMs = 30000)
    {
        if (_term is null) throw new InvalidOperationException("Terminal not ready");

        // consume sentinel from current ready prompt
        await WaitForSentinel(timeoutMs);
        var offset = _outputBuffer.Length;

        _term.WriteToTerm(command + "\r");

        // wait for sentinel from prompt after command finishes
        await WaitForSentinel(timeoutMs);
        var endIdx = _outputBuffer.ToString().LastIndexOf(_sentinel);

        var output = _outputBuffer.ToString()[offset..endIdx];

        // strip the echoed command (first line)
        var firstNewline = output.IndexOf('\n');
        if (firstNewline >= 0)
            output = output[(firstNewline + 1)..];

        return output.Trim();
    }

    private async Task WaitForSentinel(int timeoutMs = 30000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await _sentinels.Reader.ReadAsync(cts.Token);
    }

    private void ProbeNativeDeps()
    {
        // verify conpty.dll loads from the right place
        if (NativeLibrary.TryLoad("conpty", out var handle))
        {
            Debug.WriteLine("conpty.dll loaded OK");
            NativeLibrary.Free(handle);
        }
        else
        {
            MessageBox.Show("FAILED to load conpty.dll — ConPTY will not work.",
                "Missing Native DLL", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

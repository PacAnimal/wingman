using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace Wingman;

public partial class MainWindow
{
    private readonly ILogger<MainWindow> _log;
    private readonly IWindowsNative _native;
    private readonly ITerminal _terminal;

    public MainWindow(ILogger<MainWindow> log, IWindowsNative native, ITerminal terminal)
    {
        _log = log;
        _native = native;
        _terminal = terminal;
        InitializeComponent();

        if (!_native.ProbeConPTY())
            MessageBox.Show("FAILED to load conpty.dll — ConPTY will not work.",
                "Missing Native DLL", MessageBoxButton.OK, MessageBoxImage.Error);

        // intercept ctrl+c before WM_KEYDOWN reaches the native terminal hwnd — selection is still
        // active at this point; by the time InterceptInputToTermApp fires it's already cleared
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
        Closed += (_, _) => ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;

        _terminal.ProcessExited += () => Dispatcher.BeginInvoke(Close);

        // Init() must run synchronously here (UI thread) so DisconnectConPTYTerm() happens
        // before Show() → Loaded fires — otherwise the control races us with the default factory
        var initTask = _terminal.Init(Terminal);
        Task.Run(async () =>
        {
            await initTask;
            var result = await _terminal.RunCommand("dir");
            _log.LogInformation("dir output:\n{Output}", result);
        });
    }

    private void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (!_native.IsCtrlCKeyDown(ref msg)) return;

        var selected = Terminal.Terminal.GetSelectedText();
        if (string.IsNullOrEmpty(selected)) return;

        Clipboard.SetText(selected);
        handled = true; // suppress ^C — don't let it reach the terminal
    }
}

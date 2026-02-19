using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EasyWindowsTerminalControl;
using Microsoft.Extensions.Logging;
using Microsoft.Terminal.Wpf;

namespace Wingman;

public partial class MainWindow
{
    private static readonly SolidColorBrush FocusBorderBrush = new(Color.FromRgb(0x4A, 0x67, 0x85));

    private readonly ILogger<MainWindow> _log;
    private readonly IWindowsNative _native;
    private readonly ITerminal _terminal;
    private bool _alwaysOnTop;

    public MainWindow(ILogger<MainWindow> log, IWindowsNative native, ITerminal terminal, IChatService? chatService)
    {
        _log = log;
        _native = native;
        _terminal = terminal;
        InitializeComponent();

        ChatPanel.Initialize(chatService);

        if (!_native.ProbeConPTY())
            MessageBox.Show("FAILED to load conpty.dll — ConPTY will not work.",
                "Missing Native DLL", MessageBoxButton.OK, MessageBoxImage.Error);

        // intercept ctrl+c before WM_KEYDOWN reaches the native terminal hwnd — selection is still
        // active at this point; by the time InterceptInputToTermApp fires it's already cleared
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
        Closed += (_, _) => ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;

        _terminal.ProcessExited += () => Dispatcher.BeginInvoke(Close);

        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => ChatPanel.InputTextBox.Focus();

        // cursor visibility + focus outline for terminal
        Terminal.GotFocus += (_, _) =>
        {
            Terminal.IsCursorVisible = true;
            TerminalBorder.BorderBrush = FocusBorderBrush;
        };
        Terminal.LostFocus += (_, _) =>
        {
            Terminal.IsCursorVisible = false;
            TerminalBorder.BorderBrush = Brushes.Transparent;
        };

        // Init() must run synchronously here (UI thread) so DisconnectConPTYTerm() happens
        // before Show() → Loaded fires — otherwise the control races us with the default factory
        _ = InitTerminal();
    }

    private async Task InitTerminal()
    {
        await _terminal.Init(Terminal);
        // cursor hide must come after init — the ANSI escape is only received once the
        // connection is live, and Init awaits the command lock so the terminal is fully ready
        if (!Terminal.IsKeyboardFocusWithin)
            Terminal.IsCursorVisible = false;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _native.EnableDarkTitleBar(hwnd);
        _native.AddAlwaysOnTopMenu(hwnd);

        // hook WndProc for WM_SYSCOMMAND
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        // set terminal theme (campbell defaults, smaller font)
        Terminal.Theme = new TerminalTheme
        {
            DefaultBackground = EasyTerminalControl.ColorToVal(Color.FromRgb(0x0C, 0x0C, 0x0C)),
            DefaultForeground = EasyTerminalControl.ColorToVal(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            DefaultSelectionBackground = EasyTerminalControl.ColorToVal(Colors.White),
            CursorStyle = CursorStyle.BlinkingBar,
            ColorTable =
            [
                0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1,
                0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
                0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9,
                0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2,
            ],
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SYSCOMMAND = 0x0112;
        if (msg == WM_SYSCOMMAND && ((uint)wParam & 0xFFF0) == WindowsNative.WM_SYSCOMMAND_ALWAYS_ON_TOP)
        {
            _alwaysOnTop = !_alwaysOnTop;
            Topmost = _alwaysOnTop;
            _native.ToggleAlwaysOnTopCheck(hwnd, _alwaysOnTop);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;

        // toggle focus between chat input and terminal
        if (ChatPanel.InputTextBox.IsFocused)
            Terminal.Focus();
        else
            ChatPanel.InputTextBox.Focus();
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

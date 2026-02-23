using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using EasyWindowsTerminalControl.Internals;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedMethodReturnValue.Local
// ReSharper disable IdentifierTypo
// ReSharper disable UnusedMember.Local

namespace Wingman;

public interface IWindowsNative
{
    bool ProbeConPTY();
    bool IsCtrlCKeyDown(ref MSG msg);
    void EnableDarkTitleBar(IntPtr hwnd);
    void AddAlwaysOnTopMenu(IntPtr hwnd);
    void ToggleAlwaysOnTopCheck(IntPtr hwnd, bool isChecked);
    void InitializeWindow(IntPtr hwnd, Action<bool> setTopmost);
    void HookPreprocessMessage(Func<bool> isChatLogActive, Func<string?> getTerminalSelection, Action focusTerminal);
    void UnhookPreprocessMessage();
}

public partial class WindowsNative : IWindowsNative
{
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_C = 0x43;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int WM_LBUTTONDOWN = 0x0201;

    private bool _alwaysOnTop;
    private Func<bool>? _isChatLogActive;
    private Func<string?>? _getTerminalSelection;
    private Action? _focusTerminal;

    // system menu
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_BYCOMMAND = 0x00000000;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_UNCHECKED = 0x00000000;
    internal const uint WM_SYSCOMMAND_ALWAYS_ON_TOP = 0x1000;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetSystemMenu(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bRevert);

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [LibraryImport("user32.dll")]
    private static partial uint CheckMenuItem(IntPtr hMenu, uint uIDCheckItem, uint uCheck);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public bool ProbeConPTY()
    {
        // pass the assembly so single-file apps search the extraction temp dir via the runtime's native resolver
        if (NativeLibrary.TryLoad("conpty", typeof(WindowsNative).Assembly, null, out var handle))
        {
            NativeLibrary.Free(handle);
            return true;
        }
        return false;
    }

    public bool IsCtrlCKeyDown(ref MSG msg)
    {
        if (msg.message != WM_KEYDOWN || (int)msg.wParam != VK_C) return false;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return false;
        return true;
    }

    public void EnableDarkTitleBar(IntPtr hwnd)
    {
        var value = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
    }

    public void AddAlwaysOnTopMenu(IntPtr hwnd)
    {
        var menu = GetSystemMenu(hwnd, false);
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, WM_SYSCOMMAND_ALWAYS_ON_TOP, "Always on Top");
    }

    public void ToggleAlwaysOnTopCheck(IntPtr hwnd, bool isChecked)
    {
        var menu = GetSystemMenu(hwnd, false);
        _ = CheckMenuItem(menu, WM_SYSCOMMAND_ALWAYS_ON_TOP, MF_BYCOMMAND | (isChecked ? MF_CHECKED : MF_UNCHECKED));
    }

    public void InitializeWindow(IntPtr hwnd, Action<bool> setTopmost)
    {
        EnableDarkTitleBar(hwnd);
        AddAlwaysOnTopMenu(hwnd);
        HwndSource.FromHwnd(hwnd)?.AddHook((h, msg, wp, lp, ref handled) => WndProc(h, msg, wp, lp, ref handled, setTopmost));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr _, ref bool handled, Action<bool> setTopmost)
    {
        if (msg == WM_SYSCOMMAND && ((uint)wParam & 0xFFF0) == WM_SYSCOMMAND_ALWAYS_ON_TOP)
        {
            _alwaysOnTop = !_alwaysOnTop;
            setTopmost(_alwaysOnTop);
            ToggleAlwaysOnTopCheck(hwnd, _alwaysOnTop);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void HookPreprocessMessage(Func<bool> isChatLogActive, Func<string?> getTerminalSelection, Action focusTerminal)
    {
        _isChatLogActive = isChatLogActive;
        _getTerminalSelection = getTerminalSelection;
        _focusTerminal = focusTerminal;
        ComponentDispatcher.ThreadPreprocessMessage += OnPreprocessMessage;
    }

    public void UnhookPreprocessMessage() => ComponentDispatcher.ThreadPreprocessMessage -= OnPreprocessMessage;

    private void OnPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (IsCtrlCKeyDown(ref msg))
        {
            // let WPF route it to Panel_PreviewKeyDown for bubble text copy
            if (_isChatLogActive!()) return;
            var selected = _getTerminalSelection!();
            if (string.IsNullOrEmpty(selected)) return;
            Clipboard.SetText(selected);
            handled = true; // suppress ^C — don't let it reach the terminal
            return;
        }
        // mouse click on the terminal's native HWND — WPF won't fire GotFocus, so force it
        if (msg.message == WM_LBUTTONDOWN && HwndSource.FromHwnd(msg.hwnd) == null)
            _focusTerminal!();
    }
}

// prevents rider/vs debugger from stealing child process output by clearing the parent's
// redirected std handles right before CreateProcess — windows auto-duplicates these to
// child console apps (even with bInheritHandles=false + conpty) per microsoft/terminal#11276
public partial class DetachedProcessFactory : IProcessFactory
{
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    public IProcess Start(string command, nuint attributes, PseudoConsole console)
    {
        FreeConsole();
        // null handles so windows won't auto-duplicate them into the child; restore after
        // CreateProcess so Console.WriteLine / ILogger still work in the parent
        var origIn = GetStdHandle(STD_INPUT_HANDLE);
        var origOut = GetStdHandle(STD_OUTPUT_HANDLE);
        var origErr = GetStdHandle(STD_ERROR_HANDLE);
        SetStdHandle(STD_INPUT_HANDLE, IntPtr.Zero);
        SetStdHandle(STD_OUTPUT_HANDLE, IntPtr.Zero);
        SetStdHandle(STD_ERROR_HANDLE, IntPtr.Zero);
        try { return ProcessFactory.Start(command, attributes, console); }
        finally
        {
            SetStdHandle(STD_INPUT_HANDLE, origIn);
            SetStdHandle(STD_OUTPUT_HANDLE, origOut);
            SetStdHandle(STD_ERROR_HANDLE, origErr);
        }
    }
}

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
    void InitializeWindow(IntPtr hwnd, Action<bool> setTopmost, AiProviderKind? currentProvider, Action<AiProviderKind> onProviderSelected);
    void UpdateProviderCheck(IntPtr hwnd, AiProviderKind provider);
    void HookPreprocessMessage(Func<bool> isChatLogActive, Func<string?> getTerminalSelection, Action focusTerminal);
    void UnhookPreprocessMessage();
    void FlashWindow(Window window);
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
    private const uint MF_POPUP = 0x00000010;
    internal const uint WM_SYSCOMMAND_ALWAYS_ON_TOP = 0x1000;
    internal const uint WM_SYSCOMMAND_PROVIDER_OPENAI = 0x1010;
    internal const uint WM_SYSCOMMAND_PROVIDER_ANTHROPIC = 0x1020;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetSystemMenu(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bRevert);

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendPopupMenu(IntPtr hMenu, uint uFlags, IntPtr hSubMenu, string? lpNewItem);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll")]
    private static partial uint CheckMenuItem(IntPtr hMenu, uint uIDCheckItem, uint uCheck);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_TRAY = 0x00000002;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FLASHWINFO pwfi);

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
        AppendMenu(menu, MF_STRING, WM_SYSCOMMAND_ALWAYS_ON_TOP, "Always on top");
    }

    private static void AddCustomMenuItems(IntPtr hwnd, AiProviderKind? currentProvider)
    {
        var menu = GetSystemMenu(hwnd, false);

        // provider submenu
        var sub = CreatePopupMenu();
        AppendMenu(sub, MF_STRING, WM_SYSCOMMAND_PROVIDER_OPENAI, "OpenAI");
        AppendMenu(sub, MF_STRING, WM_SYSCOMMAND_PROVIDER_ANTHROPIC, "Anthropic");

        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendPopupMenu(menu, MF_POPUP, sub, "Select provider");

        // check current provider
        if (currentProvider != null)
        {
            var id = currentProvider == AiProviderKind.OpenAI
                ? WM_SYSCOMMAND_PROVIDER_OPENAI
                : WM_SYSCOMMAND_PROVIDER_ANTHROPIC;
            _ = CheckMenuItem(sub, id, MF_BYCOMMAND | MF_CHECKED);
        }

        // always on top
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, WM_SYSCOMMAND_ALWAYS_ON_TOP, "Always on top");
    }

    public void ToggleAlwaysOnTopCheck(IntPtr hwnd, bool isChecked)
    {
        var menu = GetSystemMenu(hwnd, false);
        _ = CheckMenuItem(menu, WM_SYSCOMMAND_ALWAYS_ON_TOP, MF_BYCOMMAND | (isChecked ? MF_CHECKED : MF_UNCHECKED));
    }

    public void UpdateProviderCheck(IntPtr hwnd, AiProviderKind provider)
    {
        var menu = GetSystemMenu(hwnd, false);
        // find the submenu by scanning — the submenu items are at fixed command IDs
        _ = CheckMenuItem(menu, WM_SYSCOMMAND_PROVIDER_OPENAI,
            MF_BYCOMMAND | (provider == AiProviderKind.OpenAI ? MF_CHECKED : MF_UNCHECKED));
        _ = CheckMenuItem(menu, WM_SYSCOMMAND_PROVIDER_ANTHROPIC,
            MF_BYCOMMAND | (provider == AiProviderKind.Anthropic ? MF_CHECKED : MF_UNCHECKED));
    }

    public void InitializeWindow(IntPtr hwnd, Action<bool> setTopmost, AiProviderKind? currentProvider, Action<AiProviderKind> onProviderSelected)
    {
        EnableDarkTitleBar(hwnd);
        AddCustomMenuItems(hwnd, currentProvider);
        HwndSource.FromHwnd(hwnd)?.AddHook((h, msg, wp, lp, ref handled) =>
            WndProc(h, msg, wp, lp, ref handled, setTopmost, onProviderSelected));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr _, ref bool handled,
        Action<bool> setTopmost, Action<AiProviderKind> onProviderSelected)
    {
        if (msg != WM_SYSCOMMAND) return IntPtr.Zero;

        var cmd = (uint)wParam & 0xFFF0;
        if (cmd == WM_SYSCOMMAND_ALWAYS_ON_TOP)
        {
            _alwaysOnTop = !_alwaysOnTop;
            setTopmost(_alwaysOnTop);
            ToggleAlwaysOnTopCheck(hwnd, _alwaysOnTop);
            handled = true;
        }
        else if (cmd == WM_SYSCOMMAND_PROVIDER_OPENAI)
        {
            onProviderSelected(AiProviderKind.OpenAI);
            handled = true;
        }
        else if (cmd == WM_SYSCOMMAND_PROVIDER_ANTHROPIC)
        {
            onProviderSelected(AiProviderKind.Anthropic);
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

    public void FlashWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var fwi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0
        };
        FlashWindowEx(ref fwi);
    }

    private void OnPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (IsCtrlCKeyDown(ref msg))
        {
            // let WPF route it to Panel_PreviewKeyDown for bubble text copy
            if (_isChatLogActive!()) return;
            var selected = _getTerminalSelection!();
            if (string.IsNullOrEmpty(selected)) return;
            try { Clipboard.SetText(selected); }
            catch (System.Runtime.InteropServices.COMException) { /* clipboard locked by another app */ }
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

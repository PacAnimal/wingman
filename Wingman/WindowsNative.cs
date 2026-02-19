using System.Runtime.InteropServices;
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
}

public class WindowsNative : IWindowsNative
{
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_C = 0x43;

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

using System.Reflection;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Wingman.UITests;

/// <summary>
/// Launches Wingman.exe and provides helpers for driving it via UIAutomation.
/// One instance per test fixture — launch once, reuse across tests.
/// </summary>
internal sealed class WingmanApp : IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly Application _app;
    private readonly AutomationElement _mainWindow;

    private WingmanApp(Application app, UIA3Automation automation, AutomationElement mainWindow)
    {
        _app = app;
        _automation = automation;
        _mainWindow = mainWindow;
    }

    // walk up from the test assembly until we find a dir containing both Wingman.sln and .git
    private static string FindSolutionRoot()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        while (true)
        {
            if (File.Exists(Path.Combine(dir, "Wingman.sln")) && Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir)
                throw new DirectoryNotFoundException("Could not locate solution root (Wingman.sln + .git).");
            dir = parent;
        }
    }

    private static string ExePath()
    {
        var root = FindSolutionRoot();
        return Path.Combine(root, "Wingman", "bin", "Debug",
            "net10.0-windows10.0.19041.0", "win10-x64", "Wingman.exe");
    }

    public static WingmanApp Launch()
    {
        var exe = ExePath();
        if (!File.Exists(exe))
            throw new FileNotFoundException($"Wingman.exe not found at: {exe}. Build the solution first.");

        var automation = new UIA3Automation();
        var app = Application.Launch(exe);

        app.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(15));

        // give the window a moment to render before we grab it
        Thread.Sleep(500);

        var windows = app.GetAllTopLevelWindows(automation);
        var mainWindow = windows.First();
        return new WingmanApp(app, automation, mainWindow);
    }

    /// <summary>
    /// Walks all descendants of the main window looking for an element
    /// that supports TextPattern (the native ConPTY terminal HWND) and is on-screen.
    /// Filtering by IsOffscreen ensures we read the active tab's terminal, not hidden spares.
    /// Returns the concatenated visible text, or null if not found.
    /// </summary>
    public string? FindTerminalText()
    {
        try
        {
            var all = _mainWindow.FindAllDescendants();
            foreach (var el in all)
            {
                if (!el.Patterns.Text.IsSupported) continue;
                if (el.Properties.IsOffscreen.IsSupported && el.IsOffscreen) continue;
                var pattern = el.Patterns.Text.Pattern;
                var ranges = pattern.GetVisibleRanges();
                if (ranges.Length == 0) continue;
                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < ranges.Length; i++)
                {
                    if (i > 0) sb.Append('\n');
                    sb.Append(ranges[i].GetText(-1).TrimEnd('\n', '\r'));
                }
                return sb.ToString();
            }
        }
        catch
        {
            // element may be mid-paint; caller will retry
        }
        return null;
    }

    /// <summary>
    /// Polls FindTerminalText until it contains <paramref name="expected"/>, times out, or the token is cancelled.
    /// </summary>
    public bool WaitForTerminalText(string expected, TimeSpan timeout, CancellationToken cancel = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            var text = FindTerminalText();
            if (text != null && text.Contains(expected, StringComparison.Ordinal))
                return true;
            Thread.Sleep(250);
        }
        return false;
    }

    // ReSharper disable once UnusedMember.Global
    public static void SendKeys(string keys) => FlaUI.Core.Input.Keyboard.Type(keys);

    public void Dispose()
    {
        try { _app.Close(); }
        catch
        {
            // ignored
        }

        _automation.Dispose();
    }
}

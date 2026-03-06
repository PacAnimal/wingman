using FlaUI.Core.Input;

namespace Wingman.UITests;

[TestFixture]
[Category("UI")]
public sealed class TabInitTests
{
    private WingmanApp? _app;

    [OneTimeSetUp]
    public void LaunchApp()
    {
        _app = WingmanApp.Launch();
        // wait for the initial "Wingman ready!" before any test runs
        Assert.That(
            _app.WaitForTerminalText("Wingman ready!", TimeSpan.FromSeconds(30)),
            Is.True,
            "App did not show 'Wingman ready!' within 30 s");
    }

    [OneTimeTearDown]
    public void KillApp() => _app?.Dispose();

    // --- main tab ---

    [Test]
    public void MainTab_ShowsWingmanReady()
    {
        var text = _app!.FindTerminalText();
        Assert.That(text, Does.Contain("Wingman ready!"));
    }

    [Test]
    public void MainTab_NoInitCommandEcho()
    {
        var text = _app!.FindTerminalText() ?? string.Empty;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Not.Contain("WINGMAN_SENTINEL"));
            Assert.That(text, Does.Not.Contain("function prompt"));
            Assert.That(text, Does.Not.Contain("ClearHistory"));
        }
    }

    // --- new tab ---

    [Test]
    [Order(10)]
    public void NewTab_ShowsWingmanReady()
    {
        Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_T);

        Assert.That(
            _app!.WaitForTerminalText("Wingman ready!", TimeSpan.FromSeconds(30)),
            Is.True,
            "New tab did not show 'Wingman ready!' within 30 s");

        // close the tab
        Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_W);
        Thread.Sleep(500);
    }

    [Test]
    [Order(11)]
    public void NewTab_NoInitCommandEcho()
    {
        Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_T);

        Assert.That(
            _app!.WaitForTerminalText("Wingman ready!", TimeSpan.FromSeconds(30)),
            Is.True,
            "New tab did not show 'Wingman ready!'");

        var text = _app.FindTerminalText() ?? string.Empty;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Not.Contain("WINGMAN_SENTINEL"));
            Assert.That(text, Does.Not.Contain("function prompt"));
            Assert.That(text, Does.Not.Contain("ClearHistory"));
        }

        Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_W);
        Thread.Sleep(500);
    }

    [Test]
    [Order(12)]
    public void NewTab_PromptAtCorrectColumn()
    {
        Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_T);

        Assert.That(
            _app!.WaitForTerminalText("Wingman ready!", TimeSpan.FromSeconds(30)),
            Is.True,
            "New tab did not show 'Wingman ready!'");

        // give PS a moment to render its prompt
        Thread.Sleep(1000);

        var text = _app.FindTerminalText() ?? string.Empty;

        // find the last non-empty line — that should be the PS prompt
        var lines = text.Split('\n');
        var promptLine = lines.LastOrDefault(l => l.Trim().Length > 0) ?? string.Empty;

        // PS prompt ("PS C:\...>") must start at column 0, not mid-line
        Assert.That(promptLine, Does.Not.StartWith(" "),
            $"Prompt line appears to be offset (leading spaces): '{promptLine}'");

        Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_W);
        Thread.Sleep(500);
    }

    // --- stress: sequential tab creation ---

    [Test]
    [Order(20)]
    [CancelAfter(360000)]
    public void EightTabs_AllInitialise(CancellationToken cancel)
    {
        const int tabCount = 8;

        for (var i = 1; i <= tabCount; i++)
        {
            TestContext.Progress.WriteLine($"Opening tab {i}...");
            Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_T);
            Thread.Sleep(6000);
        }

        // active tab is the last one opened; cycle backwards through all tabCount new tabs
        for (var i = tabCount; i >= 1; i--)
        {
            var ready = _app!.WaitForTerminalText("Wingman ready!", TimeSpan.FromSeconds(30), cancel);
            var text = _app.FindTerminalText() ?? string.Empty;
            TestContext.Progress.WriteLine($"Tab {i} text snapshot:\n{text}\n---");
            Assert.That(ready, Is.True, $"Tab {i} did not show 'Wingman ready!' within 30 s");

            if (i > 1)
                Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                    FlaUI.Core.WindowsAPI.VirtualKeyShort.SHIFT,
                    FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
        }

        // close all extra tabs
        for (var i = 0; i < tabCount; i++)
        {
            Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_W);
            Thread.Sleep(500);
        }
    }
}

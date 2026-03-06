using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyWindowsTerminalControl;
using Microsoft.Extensions.Logging;

namespace Wingman;

internal sealed class TabSession : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; } = "New Tab";
    public bool IsManuallyNamed { get; set; }

    public ChatPanel ChatPanel { get; }
    public EasyTerminalControl TerminalControl { get; }
    public Border TerminalBorder { get; }
    public IScreenBuffer ScreenBuffer { get; }
    public ITerminal Terminal { get; }

    public IChatService? ChatService { get; set; }
    public AgentEvents? Events { get; set; }
    public TaskDescriptionService? TaskDescription { get; set; }
    public TitleSpinner? Spinner { get; set; }

    public FocusTarget LastFocus { get; set; } = FocusTarget.Input;

    private bool _disposed;

    public TabSession(ILoggerFactory loggerFactory)
    {
        ScreenBuffer = new ScreenBuffer();
        Terminal = new Terminal(loggerFactory.CreateLogger<Terminal>(), ScreenBuffer);

        ChatPanel = new ChatPanel();
        Grid.SetColumn(ChatPanel, 0);

        TerminalControl = new EasyTerminalControl
        {
            Margin = new Thickness(8, 0, 0, 0),
            StartupCommandLine = "pwsh.exe -NoProfile",
            Win32InputMode = true,
            InputCapture = EasyTerminalControl.INPUT_CAPTURE.TabKey | EasyTerminalControl.INPUT_CAPTURE.DirectionKeys,
            FontSizeWhenSettingTheme = 11,
        };

        TerminalBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0C)),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Child = TerminalControl,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(TerminalBorder, "TerminalBorder");
        Grid.SetColumn(TerminalBorder, 2);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        TaskDescription?.Dispose();
        Spinner?.Dispose();

        if (Terminal is IDisposable disposable)
            disposable.Dispose();
    }
}

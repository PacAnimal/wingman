using System.Windows;
using System.Windows.Threading;
using Cathedral.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace Wingman;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // catch fire-and-forget Task.Run exceptions from the terminal library
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            Dispatcher.BeginInvoke(() =>
                MessageBox.Show($"Unobserved task exception:\n{args.Exception}",
                    "Task Exception", MessageBoxButton.OK, MessageBoxImage.Error));
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            MessageBox.Show($"Unhandled exception:\n{args.ExceptionObject}",
                "Fatal", MessageBoxButton.OK, MessageBoxImage.Error);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"Dispatcher exception:\n{args.Exception}",
                "UI Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _host = Host.CreateDefaultBuilder()
            .DisableEventLog()
            .ConfigureServices(services =>
            {
                services.AddSereneConsoleLogging();
                services.AddSingleton<IWindowsNative, WindowsNative>();
                services.AddSingleton<ITerminal, Terminal>();

                // AI chat: only wire up if API key is configured
                var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (!string.IsNullOrEmpty(apiKey))
                {
                    services.AddSingleton<AgentEvents>();
                    var openAiClient = new OpenAIClient(apiKey);

                    // conversation client (gpt-5.1) with function invocation middleware
                    services.AddChatClient(
                            openAiClient.GetChatClient("gpt-5.1").AsIChatClient())
                        .UseFunctionInvocation();

                    // guard client (gpt-5-mini) — registered directly, not as IChatClient
                    var guardClient = openAiClient.GetChatClient("gpt-5-mini").AsIChatClient();
                    services.AddSingleton<ICommandGuard>(sp =>
                        new CommandGuard(guardClient, sp.GetRequiredService<ILogger<CommandGuard>>()));

                    // approval UI — depends on MainWindow.ChatPanel; use Lazy<> to break circular dep
                    services.AddSingleton<IApprovalUI>(sp =>
                        new ApprovalUI(sp.GetRequiredService<MainWindow>().ChatPanel));
                    services.AddSingleton(sp => new Lazy<IApprovalUI>(() => sp.GetRequiredService<IApprovalUI>()));

                    // ask_user tool — same Lazy<ChatPanel> pattern to break circular dep
                    services.AddSingleton(sp => new Lazy<ChatPanel>(() => sp.GetRequiredService<MainWindow>().ChatPanel));

                    services.AddSingleton<IAgentTool, RunCommandTool>();
                    services.AddSingleton<IAgentTool, AskUserTool>();
                    services.AddSingleton<IChatService, ChatService>();
                }

                // factory so IChatService? resolves to null when not registered
                services.AddSingleton<MainWindow>(sp => new MainWindow(
                    sp.GetRequiredService<ILogger<MainWindow>>(),
                    sp.GetRequiredService<IWindowsNative>(),
                    sp.GetRequiredService<ITerminal>(),
                    sp.GetService<IChatService>(),
                    sp.GetService<AgentEvents>()));
            })
            .Build();

        await _host.StartAsync();

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }
}

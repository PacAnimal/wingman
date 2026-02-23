using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using Cathedral.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        // load settings and determine startup state before building the host
        var settings = new SettingsService();
        string? startupError;
        string? apiKey = null;

        try
        {
            var stored = await settings.LoadAsync();
            apiKey = stored.OpenAiApiKey;

            if (string.IsNullOrEmpty(apiKey))
            {
                startupError = "Enter your OpenAI API key to get started.";
            }
            else
            {
                var validationError = await settings.ValidateKeyAsync(apiKey);
                startupError = validationError != null ? $"API key validation failed: {validationError}" : null;
                if (startupError != null) apiKey = null;
            }
        }
        catch (CryptographicException)
        {
            startupError = "Settings file corrupted. Enter your API key again.";
            apiKey = null;
        }

        _host = Host.CreateDefaultBuilder()
            .DisableEventLog()
            .ConfigureServices(services =>
            {
                services.AddSereneConsoleLogging();
                services.AddSingleton<IWindowsNative, WindowsNative>();
                services.AddSingleton<IScreenBuffer, ScreenBuffer>();
                services.AddSingleton<ITerminal, Terminal>();
                services.AddSingleton<ISettingsService>(settings);

                services.AddSingleton<MainWindow>(sp => new MainWindow(sp.GetRequiredService<ILoggerFactory>(),
                    sp.GetRequiredService<IWindowsNative>(),
                    sp.GetRequiredService<ITerminal>(),
                    sp.GetRequiredService<ISettingsService>(),
                    sp.GetRequiredService<IScreenBuffer>(),
                    startupError));
            })
            .Build();

        await _host.StartAsync();

        // if key is valid, wire up AI immediately via ActivateAi
        var window = _host.Services.GetRequiredService<MainWindow>();
        if (!string.IsNullOrEmpty(apiKey))
            window.ActivateAi(apiKey);
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

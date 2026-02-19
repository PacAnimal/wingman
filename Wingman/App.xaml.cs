using System.Windows;
using System.Windows.Threading;
using Cathedral.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
                services.AddSingleton<MainWindow>();
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

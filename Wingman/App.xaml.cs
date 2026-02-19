using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Wingman;

public partial class App : Application
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    protected override void OnStartup(StartupEventArgs e)
    {
        FreeConsole();
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
    }
}

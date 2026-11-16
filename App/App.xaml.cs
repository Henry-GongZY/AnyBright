using Microsoft.UI.Xaml;
using System.Threading;

namespace ScreenBrightnessApp;

public partial class App : Application
{
    private Window? _window;
    private static Mutex? _singleInstanceMutex;
    private const string MutexName = "Local\\AnyBright.SingleInstance";
    private const string ActivateEventName = "Local\\AnyBright.Activate";

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // the tray instance is already running: poke its window and leave
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch { }
            Environment.Exit(0);
        }

        try { Native.SetCurrentProcessExplicitAppUserModelID("AnyBright.App"); } catch { }

        _window = new MainWindow();
        _window.Activate();

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        ThreadPool.RegisterWaitForSingleObject(activate,
            (_, _) => _window?.DispatcherQueue.TryEnqueue(() => MainWindow.Current?.BringToForeground()),
            null, -1, false);
    }
}

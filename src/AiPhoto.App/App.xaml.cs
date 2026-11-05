using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AiPhoto.App.Diagnostics;
using AiPhoto.App.Services;

namespace AiPhoto.App;

public partial class App : Application
{
    private const string InstanceName = "AiPhoto.SingleInstance.v1";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\" + InstanceName, out var isFirst);
        if (!isFirst)
        {
            // Ask the running instance to show its settings window, then exit.
            try { EventWaitHandle.OpenExisting(@"Local\" + InstanceName + ".Activate").Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        _host = new AppHost();
        _host.Start(background: e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase));

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + InstanceName + ".Activate");
        var thread = new Thread(() =>
        {
            while (_activationEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(() => _host?.OpenSettings());
            }
        }) { IsBackground = true, Name = "AiPhoto activation listener" };
        thread.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        MessageBox.Show($"发生错误：{e.Exception.Message}\n\n详细信息已写入日志：{Log.Directory}", "AiPhoto",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

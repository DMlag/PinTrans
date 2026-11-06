using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AiPhoto.App.Diagnostics;
using AiPhoto.App.Infrastructure;
using AiPhoto.App.Services;

namespace AiPhoto.App;

public partial class App : Application
{
    private const string InstanceName = "PinTrans.SingleInstance.v1";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            var quiet = e.Args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
            if (!quiet)
            {
                var exePath = Environment.ProcessPath ?? "PinTrans.exe";
                var result = MessageBox.Show(
                    $"确定要彻底卸载 PinTrans 并清除所有留存数据吗？\n\n将会移除开机自启注册表项、配置与日志 ({AppPaths.DataDirectory})、运行缓存，并删除本程序文件 ({exePath})。\n（已保存的截图图片不会被删除）",
                    "卸载 PinTrans",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.OK)
                {
                    Shutdown();
                    return;
                }
            }
            AppUninstaller.CleanDataAndShutdown(deleteExecutable: true);
            return;
        }

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
        }) { IsBackground = true, Name = "PinTrans activation listener" };
        thread.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        MessageBox.Show($"发生错误：{e.Exception.Message}\n\n详细信息已写入日志：{Log.Directory}", "PinTrans",
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

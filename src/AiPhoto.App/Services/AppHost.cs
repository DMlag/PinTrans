using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AiPhoto.App.Capture;
using AiPhoto.App.Capture.Selection;
using AiPhoto.App.Diagnostics;
using AiPhoto.App.Imaging;
using AiPhoto.App.Infrastructure;
using AiPhoto.App.LongCapture;
using AiPhoto.App.Pin;
using AiPhoto.App.Settings;
using AiPhoto.App.Viewer;
using AiPhoto.Core.Hotkeys;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Net;
using AiPhoto.Core.Settings;
using AiPhoto.Core.Snapping;
using AiPhoto.Core.Translation;
using AiPhoto.Core.Translation.Gemini;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace AiPhoto.App.Services;

/// <summary>
/// Composition root: owns settings, networking, the translator, global hotkeys and the tray icon,
/// and opens the capture / pin / viewer windows.
/// </summary>
public sealed class AppHost : IAppHost, IDisposable
{
    private readonly SettingsStore _store;
    private readonly ScreenCaptureService _capture;
    private GlobalHotkeyManager? _hotkeys;
    private TaskbarIcon? _tray;
    private HttpClient? _http;
    private GeminiImageTranslator? _translator;
    private SettingsWindow? _settingsWindow;
    private bool _capturing;
    private bool _hotkeysSuspended;

    public AppHost()
    {
        _store = new SettingsStore(AppPaths.SettingsFile, new DpapiSecretProtector());
        _capture = new ScreenCaptureService(() => Settings);
    }

    public AppSettings Settings => _store.Current;

    public IImageTranslator Translator => _translator ?? throw new InvalidOperationException("Not started.");

    public void Start(bool background)
    {
        _store.Load();
        RebuildNetwork();
        CreateTray();
        _hotkeys = new GlobalHotkeyManager();
        var failures = RegisterHotkeys();
        // While a HotkeyBox records a shortcut, registered hotkeys would be swallowed as WM_HOTKEY (and start a
        // capture) instead of reaching the box: suspend them for the duration of the recording.
        HotkeyBox.RecordingChanged += SetHotkeysSuspended;

        if (failures is not null) Notify("快捷键注册失败", failures, isError: true);
        else if (!background) Notify("AiPhoto 已在后台运行", $"按 {Settings.Hotkeys.Capture} 截图，{Settings.Hotkeys.CaptureAndTranslate} 截图翻译。右键托盘图标打开设置。");

        if (string.IsNullOrWhiteSpace(Settings.Translation.ApiKey) && !background)
        {
            OpenSettings();
        }
        Log.Info($"Started. Settings: {AppPaths.SettingsFile}");
    }

    // ------------------------------------------------------------------ capture entry points

    public void StartCapture(CaptureIntent intent)
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            // Snapshot first so menus/tooltips that are open at hotkey time are captured as they are.
            var snapMode = Settings.Capture.EffectiveSnapMode;
            var windows = snapMode == SnapMode.Disabled ? [] : WindowFinder.GetWindows(includeChildHwnds: snapMode == SnapMode.Smart);
            var sw = Stopwatch.StartNew();
            var snapshot = _capture.CaptureDesktop();
            Log.Info($"Captured {snapshot.Monitors.Count} monitor(s) {snapshot.VirtualBounds} in {sw.ElapsedMilliseconds} ms, HDR={snapshot.HasHdr}");

            var window = new CaptureWindow(snapshot, intent, this, windows);
            window.Closed += (_, _) => _capturing = false;
            window.Show();
        }
        catch (Exception ex)
        {
            _capturing = false;
            Log.Error("Capture failed", ex);
            Notify("截图失败", ex.Message, isError: true);
        }
    }

    /// <summary>Starts a capture after tray menus/flyouts had time to disappear from the screen.</summary>
    private async void StartCaptureDeferred(CaptureIntent intent)
    {
        await Task.Delay(250);
        StartCapture(intent);
    }

    public void CaptureFullScreenToClipboard()
    {
        try
        {
            var snapshot = _capture.CaptureDesktop();
            WpfImaging.CopyToClipboard(snapshot.Composite.ToBitmapSource());
            Notify("已复制全屏截图", $"{snapshot.Composite.Width} × {snapshot.Composite.Height}");
        }
        catch (Exception ex)
        {
            Log.Error("Full screen capture failed", ex);
            Notify("截图失败", ex.Message, isError: true);
        }
    }

    public void ShowPin(Bgra32Image image, PixelRect screenRect, PinMode mode = PinMode.None)
    {
        var pin = new PinWindow(image, screenRect, this, mode);
        pin.Show();
        pin.Activate();
    }

    public void ShowPin(Bgra32Image image, PixelRect screenRect, bool translate)
        => ShowPin(image, screenRect, translate ? PinMode.Translate : PinMode.None);

    public void StartLongCapture(PixelRect screenRect)
    {
        if (screenRect.Width < 20 || screenRect.Height < 20)
        {
            Notify("长截图", "选区太小。", isError: true);
            return;
        }
        var session = new LongCaptureSession(screenRect, _capture, Settings.Capture, image => ShowViewer(image, "长截图"));
        session.Start();
    }

    public void ShowViewer(Bgra32Image image, string title)
    {
        var viewer = new ImageViewerWindow(image, title, this);
        viewer.Show();
        viewer.Activate();
    }

    // ------------------------------------------------------------------ misc services

    public void Notify(string title, string message, bool isError = false)
    {
        if (!Settings.General.ShowNotifications && !isError) return;
        try
        {
            _tray?.ShowNotification(title, message, isError ? NotificationIcon.Error : NotificationIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Warn($"Notification failed: {ex.Message}");
        }
    }

    public string SuggestFilePath(string? suffix = null)
    {
        var dir = string.IsNullOrWhiteSpace(Settings.Capture.SaveDirectory) ? AppPaths.DefaultSaveDirectory : Settings.Capture.SaveDirectory;
        string name;
        try
        {
            name = string.Format(CultureInfo.InvariantCulture, Settings.Capture.FileNameTemplate, DateTime.Now);
        }
        catch (FormatException)
        {
            name = $"AiPhoto_{DateTime.Now:yyyyMMdd_HHmmss}";
        }
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        try { Directory.CreateDirectory(dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Path.Combine(dir, name + suffix);
    }

    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(Settings.DeepClone(), ApplySettings);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            SetHotkeysSuspended(false); // in case the window closed while a HotkeyBox was recording
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>Saves and applies new settings. Returns a warning for the user, or null.</summary>
    private string? ApplySettings(AppSettings settings)
    {
        try
        {
            _store.Save(settings);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
            return "保存设置失败：" + ex.Message;
        }

        RebuildNetwork();
        BuildTrayMenu();
        try
        {
            AutoStart.Apply(settings.General.StartWithWindows);
        }
        catch (Exception ex)
        {
            Log.Warn($"Auto start update failed: {ex.Message}");
        }
        return RegisterHotkeys();
    }

    private void RebuildNetwork()
    {
        var old = _http;
        try
        {
            _http = ProxyHttpClientFactory.Create(Settings.Network.ToProxySettings(), TimeSpan.FromSeconds(Math.Max(10, Settings.Network.TimeoutSeconds)));
        }
        catch (ArgumentException ex)
        {
            Log.Warn($"Invalid proxy settings, using system proxy: {ex.Message}");
            _http = ProxyHttpClientFactory.Create(new ProxySettings { Mode = ProxyMode.System });
        }
        _translator = new GeminiImageTranslator(_http, () => Settings.Translation.ToGeminiOptions());
        // Requests already in flight keep using the old client; dispose it a bit later.
        if (old is not null) _ = Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
    }

    private void SetHotkeysSuspended(bool suspended)
    {
        if (suspended == _hotkeysSuspended) return;
        _hotkeysSuspended = suspended;
        if (suspended) _hotkeys?.UnregisterAll();
        else RegisterHotkeys();
    }

    private string? RegisterHotkeys()
    {
        if (_hotkeys is null) return null;
        _hotkeys.UnregisterAll();
        var failures = new List<string>();
        void Register(string text, string name, Action action)
        {
            if (!HotkeyGesture.TryParse(text, out var gesture)) { failures.Add($"{name}：无法识别“{text}”"); return; }
            if (!_hotkeys.Register(gesture, action)) failures.Add($"{name}（{gesture}）可能已被其他程序占用");
        }

        var h = Settings.Hotkeys;
        Register(h.Capture, "截图", () => StartCapture(CaptureIntent.Normal));
        Register(h.CaptureAndTranslate, "截图翻译", () => StartCapture(CaptureIntent.Translate));
        Register(h.CaptureAndOcr, "截图识字/扫码", () => StartCapture(CaptureIntent.Ocr));
        Register(h.LongCapture, "长截图", () => StartCapture(CaptureIntent.LongCapture));
        Register(h.CaptureFullScreen, "全屏截图", CaptureFullScreenToClipboard);

        if (failures.Count == 0) return null;
        var message = string.Join("\n", failures) + "\n可在 设置 → 快捷键 中修改。";
        Log.Warn("Hotkey registration: " + message);
        return message;
    }

    // ------------------------------------------------------------------ tray

    private void CreateTray()
    {
        _tray = new TaskbarIcon
        {
            ToolTipText = "AiPhoto 截图翻译",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.ico")),
            NoLeftClickDelay = true,
        };
        _tray.TrayLeftMouseUp += (_, _) => StartCaptureDeferred(CaptureIntent.Normal);
        BuildTrayMenu();
        _tray.ForceCreate(false);
    }

    private void BuildTrayMenu()
    {
        if (_tray is null) return;
        var h = Settings.Hotkeys;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("截图", h.Capture, () => StartCaptureDeferred(CaptureIntent.Normal)));
        menu.Items.Add(MenuItem("截图并翻译", h.CaptureAndTranslate, () => StartCaptureDeferred(CaptureIntent.Translate)));
        menu.Items.Add(MenuItem("截图离线识字 / 扫码 (OCR)", h.CaptureAndOcr, () => StartCaptureDeferred(CaptureIntent.Ocr)));
        menu.Items.Add(MenuItem("长截图", h.LongCapture, () => StartCaptureDeferred(CaptureIntent.LongCapture)));
        menu.Items.Add(MenuItem("全屏截图到剪贴板", h.CaptureFullScreen, CaptureFullScreenToClipboard));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("打开截图文件夹", null, () =>
        {
            var dir = Path.GetDirectoryName(SuggestFilePath())!;
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }));
        menu.Items.Add(MenuItem("设置...", null, OpenSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("退出", null, () => Application.Current.Shutdown()));
        _tray.ContextMenu = menu;
    }

    private static MenuItem MenuItem(string header, string? gesture, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _http?.Dispose();
    }
}

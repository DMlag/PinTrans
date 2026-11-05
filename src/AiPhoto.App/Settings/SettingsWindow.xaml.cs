using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using AiPhoto.App.Diagnostics;
using AiPhoto.Core.Hotkeys;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Net;
using AiPhoto.Core.Settings;
using AiPhoto.Core.Translation.Gemini;
using Microsoft.Win32;

namespace AiPhoto.App.Settings;

public partial class SettingsWindow : Window
{
    private static readonly string[] SuggestedModels =
    [
        "gemini-flash-latest", "gemini-flash-lite-latest", "gemini-pro-latest",
    ];

    private static readonly string[] Languages =
    [
        "简体中文", "繁體中文", "English", "日本語", "한국어", "Français", "Deutsch", "Español", "Русский", "Português", "Italiano", "Tiếng Việt", "ไทย",
    ];

    private readonly AppSettings _settings;
    private readonly Func<AppSettings, string?> _save;

    /// <param name="settings">A copy of the settings that this window may modify.</param>
    /// <param name="save">Persists and applies settings; returns a warning message or null.</param>
    public SettingsWindow(AppSettings settings, Func<AppSettings, string?> save)
    {
        _settings = settings;
        _save = save;
        InitializeComponent();

        ThinkingBox.ItemsSource = new Dictionary<ThinkingLevel, string>
        {
            [ThinkingLevel.Minimal] = "最少（最快，推荐）",
            [ThinkingLevel.Low] = "低",
            [ThinkingLevel.Medium] = "中",
            [ThinkingLevel.High] = "高（更准但更慢、更贵）",
            [ThinkingLevel.ModelDefault] = "模型默认",
        };
        MediaResolutionBox.ItemsSource = new Dictionary<MediaResolution, string>
        {
            [MediaResolution.High] = "高（小字识别更好，推荐）",
            [MediaResolution.Medium] = "中",
            [MediaResolution.Low] = "低（最省 token）",
            [MediaResolution.ModelDefault] = "模型默认",
        };
        ProxyModeBox.ItemsSource = new Dictionary<ProxyMode, string>
        {
            [ProxyMode.System] = "使用系统代理",
            [ProxyMode.None] = "不使用代理（直连）",
            [ProxyMode.Http] = "HTTP 代理",
            [ProxyMode.Socks5] = "SOCKS5 代理",
        };
        ToneMapBox.ItemsSource = new Dictionary<ToneMapOperator, string>
        {
            [ToneMapOperator.Clip] = "保持 SDR 亮度（推荐，高光截断）",
            [ToneMapOperator.Reinhard] = "压缩高光（Reinhard，适合 HDR 游戏/视频）",
            [ToneMapOperator.Aces] = "电影感（ACES）",
        };
        ModelBox.ItemsSource = SuggestedModels;
        TargetLanguageBox.ItemsSource = Languages;
        FontBox.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n).ToList();

        DataContext = _settings;
        ApiKeyBox.Password = _settings.Translation.ApiKey;
        ProxyPasswordBox.Password = _settings.Network.ProxyPassword;
        HotkeyCapture.Gesture = Parse(_settings.Hotkeys.Capture);
        HotkeyTranslate.Gesture = Parse(_settings.Hotkeys.CaptureAndTranslate);
        HotkeyLong.Gesture = Parse(_settings.Hotkeys.LongCapture);
        HotkeyFull.Gesture = Parse(_settings.Hotkeys.CaptureFullScreen);
        if (string.IsNullOrWhiteSpace(_settings.Capture.SaveDirectory)) SaveDirBox.Text = AppPaths.DefaultSaveDirectory;

        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        VersionText.Text = $"AiPhoto {version.Split('+')[0]}  ·  设置文件：{AppPaths.SettingsFile}";
        UpdateProxyDetails();
    }

    private static HotkeyGesture Parse(string text) => HotkeyGesture.TryParse(text, out var g) ? g : HotkeyGesture.None;

    private string ApiKeyText => ShowKey.IsChecked == true ? ApiKeyPlain.Text : ApiKeyBox.Password;

    private void OnShowKeyChanged(object sender, RoutedEventArgs e)
    {
        if (ShowKey.IsChecked == true)
        {
            ApiKeyPlain.Text = ApiKeyBox.Password;
            ApiKeyPlain.Visibility = Visibility.Visible;
            ApiKeyBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            ApiKeyBox.Password = ApiKeyPlain.Text;
            ApiKeyBox.Visibility = Visibility.Visible;
            ApiKeyPlain.Visibility = Visibility.Collapsed;
        }
    }

    private void OnProxyModeChanged(object sender, SelectionChangedEventArgs e) => UpdateProxyDetails();

    private void UpdateProxyDetails()
    {
        if (ProxyDetails is null) return;
        var mode = ProxyModeBox.SelectedValue is ProxyMode m ? m : ProxyMode.System;
        ProxyDetails.IsEnabled = mode is ProxyMode.Http or ProxyMode.Socks5;
    }

    /// <summary>Copies values that are not data-bound (passwords, hotkeys) into the settings object.</summary>
    private void CollectUnboundValues()
    {
        _settings.Translation.ApiKey = ApiKeyText.Trim();
        _settings.Network.ProxyPassword = ProxyPasswordBox.Password;
        _settings.Network.ProxyHost = StripScheme(_settings.Network.ProxyHost ?? "");
        _settings.Hotkeys.Capture = HotkeyCapture.Gesture.ToString();
        _settings.Hotkeys.CaptureAndTranslate = HotkeyTranslate.Gesture.ToString();
        _settings.Hotkeys.LongCapture = HotkeyLong.Gesture.ToString();
        _settings.Hotkeys.CaptureFullScreen = HotkeyFull.Gesture.ToString();
        // Editable combo boxes only push Text on focus loss; read them directly.
        _settings.Translation.Model = ModelBox.Text.Trim();
        _settings.Translation.TargetLanguage = TargetLanguageBox.Text.Trim();
        _settings.Translation.OverlayFontFamily = FontBox.Text.Trim();
        if (SaveDirBox.Text.Trim() == AppPaths.DefaultSaveDirectory) _settings.Capture.SaveDirectory = "";
    }

    private static string StripScheme(string host)
    {
        host = host.Trim();
        var idx = host.IndexOf("://", StringComparison.Ordinal);
        if (idx >= 0) host = host[(idx + 3)..];
        return host.TrimEnd('/');
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        CollectUnboundValues();
        var button = (Button)sender;
        button.IsEnabled = false;
        TestResult.Foreground = Brushes.Gray;
        TestResult.Text = "正在连接…";
        try
        {
            using var http = ProxyHttpClientFactory.Create(_settings.Network.ToProxySettings(), TimeSpan.FromSeconds(Math.Max(5, _settings.Network.TimeoutSeconds)));
            var translator = new GeminiImageTranslator(http, () => _settings.Translation.ToGeminiOptions());
            var models = await translator.ListModelsAsync();
            var current = ModelBox.Text;
            ModelBox.ItemsSource = SuggestedModels.Concat(models.Where(m => m.Contains("gemini", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m)).Distinct().ToList();
            ModelBox.Text = current;
            TestResult.Foreground = Brushes.SeaGreen;
            TestResult.Text = $"连接成功，可用模型 {models.Count} 个（已加入下拉列表）。";
        }
        catch (Exception ex)
        {
            TestResult.Foreground = Brushes.Firebrick;
            TestResult.Text = "连接失败：" + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnBrowseSaveDir(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = SaveDirBox.Text, Title = "选择截图保存目录" };
        if (dialog.ShowDialog(this) == true)
        {
            SaveDirBox.Text = dialog.FolderName;
            _settings.Capture.SaveDirectory = dialog.FolderName;
        }
    }

    private void OnHyperlink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });

    private void OnOpenLogDir(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(Log.Directory) { UseShellExecute = true });

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Push pending edits of the focused text box into the binding source.
        if (Keyboard.FocusedElement is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        CollectUnboundValues();

        var gestures = new[] { HotkeyCapture.Gesture, HotkeyTranslate.Gesture, HotkeyLong.Gesture, HotkeyFull.Gesture }
            .Where(g => !g.IsEmpty).Select(g => g.ToString()).ToList();
        if (gestures.Count != gestures.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            MessageBox.Show(this, "有重复的快捷键，请修改。", "AiPhoto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_settings.Network.ProxyMode is ProxyMode.Http or ProxyMode.Socks5 &&
            (string.IsNullOrWhiteSpace(_settings.Network.ProxyHost) || _settings.Network.ProxyPort is <= 0 or > 65535))
        {
            MessageBox.Show(this, "代理地址或端口无效。", "AiPhoto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var warning = _save(_settings);
        if (warning is not null)
        {
            MessageBox.Show(this, warning, "AiPhoto", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Close();
    }
}

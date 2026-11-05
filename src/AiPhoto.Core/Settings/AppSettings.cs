using System.Text.Json.Serialization;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Net;
using AiPhoto.Core.Translation.Gemini;

namespace AiPhoto.Core.Settings;

/// <summary>
/// All user settings. Serialised to JSON; secrets are stored only in encrypted form
/// (see <see cref="SettingsStore"/> and <see cref="ISecretProtector"/>).
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GeneralSettings General { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public TranslationSettings Translation { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
    public AnnotationSettings Annotation { get; set; } = new();

    public AppSettings DeepClone() => SettingsStore.Deserialize(SettingsStore.Serialize(this, includeSecretsInMemory: true), keepSecrets: true);
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; }
    public bool ShowNotifications { get; set; } = true;
}

public sealed class HotkeySettings
{
    public string Capture { get; set; } = "F1";
    public string CaptureAndTranslate { get; set; } = "F2";
    public string LongCapture { get; set; } = "Ctrl+F1";
    public string CaptureFullScreen { get; set; } = "";
}

public sealed class CaptureSettings
{
    /// <summary>Empty = "Pictures\AiPhoto".</summary>
    public string SaveDirectory { get; set; } = "";

    /// <summary>File name without extension; <c>{0}</c> is replaced by the timestamp.</summary>
    public string FileNameTemplate { get; set; } = "AiPhoto_{0:yyyyMMdd_HHmmss}";

    public bool ShowMagnifier { get; set; } = true;
    public bool SnapToWindows { get; set; } = true;
    public bool CopyAfterSave { get; set; }

    /// <summary>Automatically save a .jxr next to the PNG when the capture comes from an HDR display.</summary>
    public bool AutoSaveHdr { get; set; }

    /// <summary>
    /// Clip keeps SDR content (web pages, apps) exactly as bright as on screen; HDR highlights are clipped.
    /// Reinhard/ACES preserve highlight detail (games, HDR video) at the cost of slightly dimmer SDR white.
    /// </summary>
    public ToneMapOperator HdrToneMapping { get; set; } = ToneMapOperator.Clip;

    /// <summary>0 = read the Windows "SDR content brightness" per monitor.</summary>
    public int HdrSdrWhiteNitsOverride { get; set; }

    public int LongCaptureWheelNotches { get; set; } = 2;
    public int LongCaptureSettleMs { get; set; } = 200;
}

public sealed class TranslationSettings
{
    public string Provider { get; set; } = "Gemini";

    [JsonIgnore] public string ApiKey { get; set; } = "";
    public string? ApiKeyEncrypted { get; set; }

    public string Model { get; set; } = GeminiOptions.DefaultModel;
    public string BaseUrl { get; set; } = GeminiOptions.DefaultBaseUrl;
    public string TargetLanguage { get; set; } = "简体中文";
    public ThinkingLevel Thinking { get; set; } = ThinkingLevel.Minimal;
    public MediaResolution MediaResolution { get; set; } = MediaResolution.High;
    public double? Temperature { get; set; }
    public bool SkipTextInTargetLanguage { get; set; } = true;
    public string CustomInstructions { get; set; } = "";

    /// <summary>Images are down-scaled so their longest edge is at most this many pixels before upload.</summary>
    public int MaxImageEdge { get; set; } = 2048;

    public string OverlayFontFamily { get; set; } = "Microsoft YaHei UI";

    public GeminiOptions ToGeminiOptions() => new()
    {
        ApiKey = ApiKey,
        Model = string.IsNullOrWhiteSpace(Model) ? GeminiOptions.DefaultModel : Model.Trim(),
        BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? GeminiOptions.DefaultBaseUrl : BaseUrl.Trim(),
        Thinking = Thinking,
        MediaResolution = MediaResolution,
        Temperature = Temperature,
        SkipTextInTargetLanguage = SkipTextInTargetLanguage,
        CustomInstructions = CustomInstructions,
    };
}

public sealed class NetworkSettings
{
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;
    public string ProxyHost { get; set; } = "127.0.0.1";
    public int ProxyPort { get; set; } = 7890;
    public string ProxyUsername { get; set; } = "";

    [JsonIgnore] public string ProxyPassword { get; set; } = "";
    public string? ProxyPasswordEncrypted { get; set; }

    public int TimeoutSeconds { get; set; } = 120;

    public ProxySettings ToProxySettings() => new()
    {
        Mode = ProxyMode,
        Host = ProxyHost.Trim(),
        Port = ProxyPort,
        Username = string.IsNullOrWhiteSpace(ProxyUsername) ? null : ProxyUsername,
        Password = ProxyPassword,
    };
}

public sealed class AnnotationSettings
{
    public string Color { get; set; } = "#FFE53935";
    public double StrokeWidth { get; set; } = 3;
    public double FontSize { get; set; } = 18;
    public int MosaicBlockSize { get; set; } = 10;
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiPhoto.Core.Settings;

/// <summary>Encrypts secrets at rest (DPAPI on Windows).</summary>
public interface ISecretProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedText);
}

/// <summary>No-op protector used in tests / non-Windows environments.</summary>
public sealed class PlainTextSecretProtector : ISecretProtector
{
    public string Protect(string plainText) => plainText;
    public string Unprotect(string protectedText) => protectedText;
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON, encrypting secrets with an <see cref="ISecretProtector"/>.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly ISecretProtector _protector;

    public SettingsStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    public string FilePath => _path;

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(_path) ? Deserialize(File.ReadAllText(_path), keepSecrets: false) : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep a copy of the broken file and start with defaults rather than crashing on startup.
            TryBackupCorruptFile();
            settings = new AppSettings();
        }

        settings.Translation.ApiKey = TryUnprotect(settings.Translation.ApiKeyEncrypted);
        settings.Network.ProxyPassword = TryUnprotect(settings.Network.ProxyPasswordEncrypted);
        Current = settings;
        return settings;
    }

    public void Save(AppSettings settings)
    {
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        settings.Translation.ApiKeyEncrypted = string.IsNullOrEmpty(settings.Translation.ApiKey) ? null : _protector.Protect(settings.Translation.ApiKey);
        settings.Network.ProxyPasswordEncrypted = string.IsNullOrEmpty(settings.Network.ProxyPassword) ? null : _protector.Protect(settings.Network.ProxyPassword);

        // GetDirectoryName returns "" for a bare file name, and CreateDirectory("") throws.
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, Serialize(settings, includeSecretsInMemory: false));
        File.Move(tmp, _path, overwrite: true);

        Current = settings;
        Changed?.Invoke(this, settings);
    }

    public static string Serialize(AppSettings settings, bool includeSecretsInMemory)
    {
        if (!includeSecretsInMemory) return JsonSerializer.Serialize(settings, JsonOptions);

        // Used for deep cloning: carry the plain secrets through an envelope that is never written to disk.
        var envelope = new CloneEnvelope(settings, settings.Translation.ApiKey, settings.Network.ProxyPassword);
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    public static AppSettings Deserialize(string json, bool keepSecrets)
    {
        if (keepSecrets)
        {
            var envelope = JsonSerializer.Deserialize<CloneEnvelope>(json, JsonOptions)!;
            var settings = FillMissingSections(envelope.Settings ?? new AppSettings());
            settings.Translation.ApiKey = envelope.ApiKey ?? "";
            settings.Network.ProxyPassword = envelope.ProxyPassword ?? "";
            return settings;
        }
        return FillMissingSections(JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings());
    }

    /// <summary>
    /// A hand-edited file may contain e.g. <c>"translation": null</c>; System.Text.Json then overwrites the
    /// initialised section with null, which used to crash <see cref="Load"/> with a NullReferenceException.
    /// </summary>
    private static AppSettings FillMissingSections(AppSettings s)
    {
        s.General ??= new GeneralSettings();
        s.Hotkeys ??= new HotkeySettings();
        s.Capture ??= new CaptureSettings();
        s.Translation ??= new TranslationSettings();
        s.Network ??= new NetworkSettings();
        s.Annotation ??= new AnnotationSettings();
        return s;
    }

    private string TryUnprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return _protector.Unprotect(value); }
        catch { return ""; } // e.g. settings copied from another Windows user
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (File.Exists(_path)) File.Copy(_path, _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record CloneEnvelope(AppSettings Settings, string? ApiKey, string? ProxyPassword);
}

using System.Text;
using System.Text.Json.Nodes;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Net;
using AiPhoto.Core.Settings;
using AiPhoto.Core.Translation.Gemini;

namespace AiPhoto.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiphoto-tests-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "sub", "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Reversible fake "encryption": reversed base64 with a marker prefix.</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plainText)
            => "enc:" + new string(Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText)).Reverse().ToArray());

        public string Unprotect(string protectedText)
        {
            if (!protectedText.StartsWith("enc:", StringComparison.Ordinal)) throw new FormatException("not protected by us");
            return Encoding.UTF8.GetString(Convert.FromBase64String(new string(protectedText[4..].Reverse().ToArray())));
        }
    }

    private const string ApiKey = "AIzaSyD-SECRET-api-key-1234567890";
    private const string ProxyPassword = "p@ss w0rd \"quoted\" 密码";

    private static AppSettings Sample()
    {
        var s = new AppSettings();
        s.General.StartWithWindows = true;
        s.Hotkeys.Capture = "Ctrl+Shift+A";
        s.Capture.SaveDirectory = @"D:\Shots";
        s.Capture.HdrToneMapping = ToneMapOperator.Aces;
        s.Translation.ApiKey = ApiKey;
        s.Translation.Model = "gemini-3-pro-preview";
        s.Translation.Thinking = ThinkingLevel.High;
        s.Translation.Temperature = 0.7;
        s.Network.ProxyMode = ProxyMode.Socks5;
        s.Network.ProxyPort = 1080;
        s.Network.ProxyUsername = "user";
        s.Network.ProxyPassword = ProxyPassword;
        s.Annotation.StrokeWidth = 5.5;
        return s;
    }

    [Fact]
    public void SaveLoad_RoundTrip_SecretsEncryptedAtRest()
    {
        var store = new SettingsStore(SettingsPath, new FakeProtector());
        AppSettings? changed = null;
        store.Changed += (_, s) => changed = s;
        var settings = Sample();

        store.Save(settings);

        Assert.Same(settings, changed);
        Assert.Same(settings, store.Current);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
        var text = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain(ApiKey, text);
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("w0rd", text);
        Assert.DoesNotContain("\"apiKey\"", text);
        Assert.DoesNotContain("\"proxyPassword\"", text);
        var json = JsonNode.Parse(text)!;
        Assert.StartsWith("enc:", json["translation"]!["apiKeyEncrypted"]!.GetValue<string>());
        Assert.StartsWith("enc:", json["network"]!["proxyPasswordEncrypted"]!.GetValue<string>());
        Assert.Equal("Socks5", json["network"]!["proxyMode"]!.GetValue<string>()); // enums as strings

        var loaded = new SettingsStore(SettingsPath, new FakeProtector()).Load();
        Assert.Equal(ApiKey, loaded.Translation.ApiKey);
        Assert.Equal(ProxyPassword, loaded.Network.ProxyPassword);
        Assert.True(loaded.General.StartWithWindows);
        Assert.Equal("Ctrl+Shift+A", loaded.Hotkeys.Capture);
        Assert.Equal(@"D:\Shots", loaded.Capture.SaveDirectory);
        Assert.Equal(ToneMapOperator.Aces, loaded.Capture.HdrToneMapping);
        Assert.Equal("gemini-3-pro-preview", loaded.Translation.Model);
        Assert.Equal(ThinkingLevel.High, loaded.Translation.Thinking);
        Assert.Equal(0.7, loaded.Translation.Temperature);
        Assert.Equal(ProxyMode.Socks5, loaded.Network.ProxyMode);
        Assert.Equal(1080, loaded.Network.ProxyPort);
        Assert.Equal("user", loaded.Network.ProxyUsername);
        Assert.Equal(5.5, loaded.Annotation.StrokeWidth);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void Save_EmptySecrets_AreNotWritten_AndLoadAsEmpty()
    {
        var store = new SettingsStore(SettingsPath, new FakeProtector());
        var s = new AppSettings();
        s.Translation.ApiKeyEncrypted = "stale";
        store.Save(s);
        var text = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("apiKeyEncrypted", text);
        Assert.DoesNotContain("proxyPasswordEncrypted", text);

        var loaded = store.Load();
        Assert.Equal("", loaded.Translation.ApiKey);
        Assert.Equal("", loaded.Network.ProxyPassword);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new SettingsStore(SettingsPath, new FakeProtector());
        var s = store.Load();
        Assert.Equal("F1", s.Hotkeys.Capture);
        Assert.Equal(GeminiOptions.DefaultModel, s.Translation.Model);
        Assert.Same(s, store.Current);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultsAndCreatesBackup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        const string garbage = "{ \"general\": { \"startWithWindows\": tru";
        File.WriteAllText(SettingsPath, garbage);

        var s = new SettingsStore(SettingsPath, new FakeProtector()).Load();

        Assert.False(s.General.StartWithWindows);
        Assert.Equal("F1", s.Hotkeys.Capture);
        var backups = Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!, "settings.json.corrupt-*");
        var backup = Assert.Single(backups);
        Assert.Equal(garbage, File.ReadAllText(backup));
    }

    [Fact]
    public void Load_InvalidEnumValue_IsTreatedAsCorrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "network": { "proxyMode": "Carrier-Pigeon" } }""");
        var s = new SettingsStore(SettingsPath, new FakeProtector()).Load();
        Assert.Equal(ProxyMode.System, s.Network.ProxyMode);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Load_PartialFile_KeepsDefaultsForMissingValues_AndToleratesCommentsAndTrailingCommas()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """
            {
              // user edited
              "hotkeys": { "capture": "F5", },
              "unknownSection": { "x": 1 },
            }
            """);
        var s = new SettingsStore(SettingsPath, new FakeProtector()).Load();
        Assert.Equal("F5", s.Hotkeys.Capture);
        Assert.Equal("F2", s.Hotkeys.CaptureAndTranslate);
        Assert.Equal(7890, s.Network.ProxyPort);
    }

    [Fact]
    public void Load_NullSections_FallBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "translation": null, "network": null, "hotkeys": null }""");
        var s = new SettingsStore(SettingsPath, new FakeProtector()).Load();
        Assert.NotNull(s.Translation);
        Assert.NotNull(s.Network);
        Assert.NotNull(s.Hotkeys);
        Assert.Equal("", s.Translation.ApiKey);
    }

    [Fact]
    public void Load_UndecryptableSecret_BecomesEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "translation": { "apiKeyEncrypted": "from-another-user", "model": "m" } }""");
        var s = new SettingsStore(SettingsPath, new FakeProtector()).Load();
        Assert.Equal("", s.Translation.ApiKey);
        Assert.Equal("m", s.Translation.Model);
    }

    [Fact]
    public void Save_RelativePathWithoutDirectory_Works()
    {
        var previous = Environment.CurrentDirectory;
        Directory.CreateDirectory(_dir);
        try
        {
            Environment.CurrentDirectory = _dir;
            var store = new SettingsStore("settings.json", new PlainTextSecretProtector());
            store.Save(new AppSettings());
            Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void DeepClone_KeepsSecretsAndIsIndependent()
    {
        var original = Sample();
        var clone = original.DeepClone();

        Assert.NotSame(original, clone);
        Assert.NotSame(original.Translation, clone.Translation);
        Assert.Equal(ApiKey, clone.Translation.ApiKey);
        Assert.Equal(ProxyPassword, clone.Network.ProxyPassword);
        Assert.Equal(original.Hotkeys.Capture, clone.Hotkeys.Capture);
        Assert.Equal(original.Capture.HdrToneMapping, clone.Capture.HdrToneMapping);
        Assert.Equal(original.Translation.Temperature, clone.Translation.Temperature);

        clone.Translation.ApiKey = "changed";
        clone.Hotkeys.Capture = "F12";
        clone.Network.ProxyPort = 1;
        Assert.Equal(ApiKey, original.Translation.ApiKey);
        Assert.Equal("Ctrl+Shift+A", original.Hotkeys.Capture);
        Assert.Equal(1080, original.Network.ProxyPort);
    }

    [Fact]
    public void Serialize_WithoutSecrets_NeverContainsPlainSecrets()
    {
        var json = SettingsStore.Serialize(Sample(), includeSecretsInMemory: false);
        Assert.DoesNotContain(ApiKey, json);
        Assert.DoesNotContain("w0rd", json);
    }

    [Fact]
    public void ToGeminiOptions_And_ToProxySettings()
    {
        var s = Sample();
        s.Translation.Model = "  ";
        s.Translation.BaseUrl = " https://relay.example.com ";
        var g = s.Translation.ToGeminiOptions();
        Assert.Equal(ApiKey, g.ApiKey);
        Assert.Equal(GeminiOptions.DefaultModel, g.Model);
        Assert.Equal("https://relay.example.com", g.BaseUrl);
        Assert.Equal(ThinkingLevel.High, g.Thinking);

        s.Network.ProxyHost = " 10.0.0.1 ";
        var p = s.Network.ToProxySettings();
        Assert.Equal("10.0.0.1", p.Host);
        Assert.Equal(ProxyMode.Socks5, p.Mode);
        Assert.Equal("user", p.Username);
        Assert.Equal(ProxyPassword, p.Password);

        s.Network.ProxyUsername = " ";
        Assert.Null(s.Network.ToProxySettings().Username);
    }

    [Fact]
    public void PlainTextProtector_IsIdentity()
    {
        var p = new PlainTextSecretProtector();
        Assert.Equal("abc", p.Unprotect(p.Protect("abc")));
    }

    [Fact]
    public void DefaultFileNameTemplate_UsesPinTransPrefix()
    {
        var s = new AppSettings();
        Assert.StartsWith("PinTrans_", s.Capture.FileNameTemplate);
    }

    [Fact]
    public void UninstallHelper_IsStandaloneExecutable_RejectsDotnetHost()
    {
        Assert.False(UninstallHelper.IsStandaloneExecutable(null));
        Assert.False(UninstallHelper.IsStandaloneExecutable(""));
        Assert.False(UninstallHelper.IsStandaloneExecutable(@"C:\Program Files\dotnet\dotnet.exe"));
        Assert.False(UninstallHelper.IsStandaloneExecutable("/usr/share/dotnet/dotnet"));
        Assert.True(UninstallHelper.IsStandaloneExecutable(@"C:\Tools\PinTrans\PinTrans.exe"));
    }

    [Fact]
    public void UninstallHelper_CleanDirectoriesImmediate_RemovesExistingDirectories()
    {
        var d1 = Path.Combine(_dir, "data1");
        var d2 = Path.Combine(_dir, "data2");
        Directory.CreateDirectory(Path.Combine(d1, "logs"));
        File.WriteAllText(Path.Combine(d1, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(d1, "logs", "pintrans.log"), "ok");
        Directory.CreateDirectory(d2);

        UninstallHelper.CleanDirectoriesImmediate([d1, d2, Path.Combine(_dir, "nonexistent")]);

        Assert.False(Directory.Exists(d1));
        Assert.False(Directory.Exists(d2));
    }

    [Fact]
    public void UninstallHelper_BuildDelayedCleanupArguments_IncludesDirsAndSelfDeleteOnlyWhenStandalone()
    {
        var dirs = new[] { @"C:\Users\Test\AppData\Roaming\PinTrans", @"C:\Users\Test\AppData\Local\Temp\.net\PinTrans" };

        var dataOnlyArgs = UninstallHelper.BuildDelayedCleanupArguments(dirs, null);
        Assert.Contains(@"rmdir /s /q ""C:\Users\Test\AppData\Roaming\PinTrans""", dataOnlyArgs);
        Assert.Contains(@"rmdir /s /q ""C:\Users\Test\AppData\Local\Temp\.net\PinTrans""", dataOnlyArgs);
        Assert.DoesNotContain("del /f /q", dataOnlyArgs);

        var dotnetHostArgs = UninstallHelper.BuildDelayedCleanupArguments(dirs, @"C:\Program Files\dotnet\dotnet.exe");
        Assert.DoesNotContain("del /f /q", dotnetHostArgs);

        var fullUninstallArgs = UninstallHelper.BuildDelayedCleanupArguments(dirs, @"D:\Apps\PinTrans\PinTrans.exe");
        Assert.Contains(@"del /f /q ""D:\Apps\PinTrans\PinTrans.exe""", fullUninstallArgs);
        Assert.Contains(@"rmdir ""D:\Apps\PinTrans""", fullUninstallArgs);
    }
}

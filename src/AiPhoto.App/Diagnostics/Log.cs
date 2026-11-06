using System.Diagnostics;
using System.Text;

namespace AiPhoto.App.Diagnostics;

/// <summary>Tiny thread-safe file logger (%AppData%\PinTrans\logs). Keeps the last 7 days.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _directory;
    private static volatile bool _disabled;

    public static string Directory => _directory ??= InitDirectory();

    /// <summary>Stops all subsequent file logging (used when uninstalling or wiping app data).</summary>
    public static void Disable() => _disabled = true;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        if (_disabled) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        Debug.Write(line);
        try
        {
            lock (Gate)
            {
                if (_disabled) return;
                File.AppendAllText(Path.Combine(Directory, $"pintrans-{DateTime.Now:yyyyMMdd}.log"), line, Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string InitDirectory()
    {
        var dir = Path.Combine(AppPaths.DataDirectory, "logs");
        if (_disabled) return dir;
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            foreach (var file in new DirectoryInfo(dir).GetFiles("*.log"))
            {
                if (file.LastWriteTime < DateTime.Now.AddDays(-7)) file.Delete();
            }
        }
        catch (IOException) { }
        return dir;
    }
}

public static class AppPaths
{
    public const string AppName = "PinTrans";
    public const string LegacyAppName = "AiPhoto";

    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string LegacyDataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyAppName);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LegacySettingsFile => Path.Combine(LegacyDataDirectory, "settings.json");

    public static string ModelsDirectory => Path.Combine(DataDirectory, "models", "ppocrv4");

    public static string DefaultSaveDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppName);

    /// <summary>
    /// Returns all application data and .NET single-file extraction directories (including legacy AiPhoto paths)
    /// that should be removed when cleaning data or uninstalling.
    /// </summary>
    public static IReadOnlyList<string> GetCleanableDirectories()
    {
        var tempRoot = Path.GetTempPath();
        return
        [
            DataDirectory,
            LegacyDataDirectory,
            Path.Combine(tempRoot, ".net", AppName),
            Path.Combine(tempRoot, ".net", LegacyAppName),
        ];
    }
}

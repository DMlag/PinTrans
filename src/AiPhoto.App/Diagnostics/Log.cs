using System.Diagnostics;
using System.Text;

namespace AiPhoto.App.Diagnostics;

/// <summary>Tiny thread-safe file logger (%AppData%\AiPhoto\logs). Keeps the last 7 days.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _directory;

    public static string Directory => _directory ??= InitDirectory();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        Debug.Write(line);
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(Directory, $"aiphoto-{DateTime.Now:yyyyMMdd}.log"), line, Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string InitDirectory()
    {
        var dir = Path.Combine(AppPaths.DataDirectory, "logs");
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            foreach (var file in new DirectoryInfo(dir).GetFiles("aiphoto-*.log"))
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
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AiPhoto");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string DefaultSaveDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AiPhoto");
}

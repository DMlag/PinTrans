using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using AiPhoto.App.Diagnostics;
using AiPhoto.Core.Settings;
using Microsoft.Win32;

namespace AiPhoto.App.Infrastructure;

/// <summary>Encrypts secrets with Windows DPAPI (current user scope).</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PinTrans.Secrets.v1");
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("AiPhoto.Secrets.v1");

    public string Protect(string plainText)
        => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedText)
    {
        var bytes = Convert.FromBase64String(protectedText);
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, LegacyEntropy, DataProtectionScope.CurrentUser));
        }
    }
}

/// <summary>"Start with Windows" via the HKCU Run key.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PinTrans";
    private const string LegacyValueName = "AiPhoto";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key.GetValue(LegacyValueName) is not null)
        {
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (exe is not null) key.SetValue(ValueName, $"\"{exe}\" --background");
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static void RemoveAll()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch (Exception)
        {
            // Ignore registry errors during uninstall cleanup.
        }
    }
}

/// <summary>Handles wiping persisted application data and optionally self-deleting the portable executable.</summary>
public static class AppUninstaller
{
    public static void CleanDataAndShutdown(bool deleteExecutable)
    {
        Log.Disable();
        AutoStart.RemoveAll();

        var dirs = AppPaths.GetCleanableDirectories();
        UninstallHelper.CleanDirectoriesImmediate(dirs);

        try
        {
            var tempDir = Path.GetTempPath();
            Environment.CurrentDirectory = tempDir;

            var exeToDelete = deleteExecutable ? Environment.ProcessPath : null;
            var args = UninstallHelper.BuildDelayedCleanupArguments(dirs, exeToDelete);
            Process.Start(new ProcessStartInfo("cmd.exe", args)
            {
                WorkingDirectory = tempDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception)
        {
            // Best-effort background cleanup.
        }

        if (Application.Current is { } app)
        {
            app.Shutdown();
        }
        else
        {
            Environment.Exit(0);
        }
    }
}

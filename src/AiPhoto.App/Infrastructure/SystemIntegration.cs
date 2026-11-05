using System.Security.Cryptography;
using System.Text;
using AiPhoto.Core.Settings;
using Microsoft.Win32;

namespace AiPhoto.App.Infrastructure;

/// <summary>Encrypts secrets with Windows DPAPI (current user scope).</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AiPhoto.Secrets.v1");

    public string Protect(string plainText)
        => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedText)
        => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser));
}

/// <summary>"Start with Windows" via the HKCU Run key.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AiPhoto";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
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
}

using System.Security.Cryptography;
using System.Text;

namespace LedVisual.Core.Services;

public interface IApiKeyStore
{
    void SaveApiKey(string key);
    string? LoadApiKey();
    void DeleteApiKey();
}

/// <summary>
/// Persists the Google AI Studio API key encrypted with Windows DPAPI (CurrentUser scope)
/// at %APPDATA%\LedVisual\api.key.bin.
/// </summary>
public sealed class SecretStore : IApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("com.led-visual.app.google-ai-studio-api-key.v1");
    private readonly string _filePath;

    public SecretStore(string? filePath = null)
    {
        _filePath = filePath ?? GetDefaultFilePath();
    }

    public static string GetDefaultFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "LedVisual", "api.key.bin");
    }

    public void SaveApiKey(string key)
    {
        var trimmed = key.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            DeleteApiKey();
            return;
        }

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var plainBytes = Encoding.UTF8.GetBytes(trimmed);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_filePath, protectedBytes);
    }

    public string? LoadApiKey()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(_filePath);
        if (protectedBytes.Length == 0)
        {
            return null;
        }

        var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        var key = Encoding.UTF8.GetString(plainBytes).Trim();
        return string.IsNullOrEmpty(key) ? null : key;
    }

    public void DeleteApiKey()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}

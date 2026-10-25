using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cylt.Core;

public sealed class LocalStore(string root)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public LauncherSettings LoadSettings()
    {
        var file = Path.Combine(root, "settings.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(file)) ?? new() : new();
    }

    public void SaveSettings(LauncherSettings settings)
    {
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "settings.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(file + ".tmp", file, true);
    }

    // Each value is encrypted for the current Windows user; no plaintext token cache is written.
    public void SaveSecret(string slot, string value)
    {
        var file = SecretPath(slot);
        Directory.CreateDirectory(root);
        byte[] clear = Encoding.UTF8.GetBytes(value);
        try
        {
            var encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(file + ".tmp", encrypted);
            File.Move(file + ".tmp", file, true);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public string? LoadSecret(string slot)
    {
        var file = SecretPath(slot);
        if (!File.Exists(file)) return null;
        byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public void ForgetSecret(string slot) => File.Delete(SecretPath(slot));
    private string SecretPath(string slot)
    {
        if (slot is not ("account" or "curseforge")) throw new ArgumentException("Unknown credential slot.", nameof(slot));
        return Path.Combine(root, slot + ".secrets");
    }
}

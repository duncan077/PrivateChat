using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatApp.Core.Abstractions;

namespace ChatApp.Core.Platform;

/// <summary>
/// Desktop <see cref="ISecureStore"/>.
/// <para>
/// On Windows each value is encrypted with DPAPI under the current user account, so
/// another user on the same machine cannot read the auth token. On Linux there is no
/// equivalent dependency-free primitive here, so values are written to a file with
/// owner-only permissions instead — that protects against other local users but
/// <b>not</b> against anything running as this user. Swap in libsecret if that threat
/// model matters.
/// </para>
/// <para>
/// The Android head supplies a Keystore-backed implementation instead of this.
/// </para>
/// </summary>
public sealed class FileSecureStore : ISecureStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ChatFlow.SecureStore.v1");

    private readonly string _filePath;
    private readonly Lock _gate = new();
    private Dictionary<string, string> _values;

    public FileSecureStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultPath();
        _values = Load();
    }

    public static string DefaultPath()
    {
        var root = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create);

        var dir = Path.Combine(root, "ChatFlow");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "secrets.dat");
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out var stored))
                return Task.FromResult<string?>(null);

            try
            {
                return Task.FromResult<string?>(Unprotect(stored));
            }
            catch
            {
                // Ciphertext written by a different user/machine, or a corrupt entry.
                _values.Remove(key);
                Save();
                return Task.FromResult<string?>(null);
            }
        }
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _values[key] = Protect(value);
            Save();
        }

        return Task.CompletedTask;
    }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            if (!_values.Remove(key)) return false;
            Save();
            return true;
        }
    }

    public void RemoveAll()
    {
        lock (_gate)
        {
            _values.Clear();
            Save();
        }
    }

    private static string Protect(string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);

        if (OperatingSystem.IsWindows())
            return Convert.ToBase64String(ProtectWindows(plain));

        return Convert.ToBase64String(plain);
    }

    private static string Unprotect(string stored)
    {
        var raw = Convert.FromBase64String(stored);

        if (OperatingSystem.IsWindows())
            return Encoding.UTF8.GetString(UnprotectWindows(raw));

        return Encoding.UTF8.GetString(raw);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] plain)
        => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] cipher)
        => ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_values);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, json);
            RestrictToOwner(temp);
            File.Move(temp, _filePath, overwrite: true);
        }
        catch
        {
            // Losing the persisted session is recoverable — the user signs in again.
        }
    }

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort; some filesystems reject mode changes.
        }
    }
}

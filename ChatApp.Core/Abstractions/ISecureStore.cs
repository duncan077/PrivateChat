namespace ChatApp.Core.Abstractions;

/// <summary>
/// Replacement for MAUI's <c>SecureStorage.Default</c>.
/// Backed by DPAPI on Windows, Keystore on Android, and a permission-restricted file
/// on Linux.
/// </summary>
public interface ISecureStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    bool Remove(string key);

    void RemoveAll();
}

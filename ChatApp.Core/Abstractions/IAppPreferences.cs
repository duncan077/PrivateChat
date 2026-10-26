namespace ChatApp.Core.Abstractions;

/// <summary>
/// Replacement for MAUI's <c>Preferences.Default</c>.
/// Non-sensitive, roaming-free key/value settings.
/// Implementations must be safe to call from any thread.
/// </summary>
public interface IAppPreferences
{
    bool ContainsKey(string key);

    string? Get(string key, string? defaultValue);
    bool Get(string key, bool defaultValue);
    int Get(string key, int defaultValue);
    long Get(string key, long defaultValue);
    double Get(string key, double defaultValue);

    void Set(string key, string? value);
    void Set(string key, bool value);
    void Set(string key, int value);
    void Set(string key, long value);
    void Set(string key, double value);

    void Remove(string key);
    void Clear();
}

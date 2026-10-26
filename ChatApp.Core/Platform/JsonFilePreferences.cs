using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ChatApp.Core.Abstractions;

namespace ChatApp.Core.Platform;

/// <summary>
/// Desktop <see cref="IAppPreferences"/> backed by a JSON file under the user's
/// application-data directory. Values are held in memory and flushed on a short
/// debounce so that chatty callers (volume sliders) do not hammer the disk.
/// </summary>
public sealed class JsonFilePreferences : IAppPreferences, IDisposable
{
    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, string> _values;
    private readonly Lock _writeGate = new();
    private readonly Timer _flushTimer;
    private volatile bool _dirty;
    private bool _disposed;

    public JsonFilePreferences(string? filePath = null)
    {
        _filePath = filePath ?? DefaultPath();
        _values = Load(_filePath);
        _flushTimer = new Timer(_ => FlushIfDirty(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public static string DefaultPath()
    {
        var root = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create);

        var dir = Path.Combine(root, "ChatFlow");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "preferences.json");
    }

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public string? Get(string key, string? defaultValue)
        => _values.TryGetValue(key, out var raw) ? raw : defaultValue;

    public bool Get(string key, bool defaultValue)
        => _values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var v) ? v : defaultValue;

    public int Get(string key, int defaultValue)
        => _values.TryGetValue(key, out var raw)
           && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : defaultValue;

    public long Get(string key, long defaultValue)
        => _values.TryGetValue(key, out var raw)
           && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : defaultValue;

    public double Get(string key, double defaultValue)
        => _values.TryGetValue(key, out var raw)
           && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : defaultValue;

    public void Set(string key, string? value)
    {
        if (value is null)
        {
            Remove(key);
            return;
        }

        _values[key] = value;
        _dirty = true;
    }

    public void Set(string key, bool value) => Set(key, value ? "true" : "false");

    public void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public void Set(string key, long value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public void Set(string key, double value) => Set(key, value.ToString("R", CultureInfo.InvariantCulture));

    public void Remove(string key)
    {
        if (_values.TryRemove(key, out _))
            _dirty = true;
    }

    public void Clear()
    {
        _values.Clear();
        _dirty = true;
    }

    private static ConcurrentDictionary<string, string> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return parsed is null
                ? new ConcurrentDictionary<string, string>(StringComparer.Ordinal)
                : new ConcurrentDictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            // A corrupt or unreadable preferences file must not stop the app from
            // starting — fall back to defaults and let the next write repair it.
            return new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void FlushIfDirty()
    {
        if (!_dirty) return;

        lock (_writeGate)
        {
            if (!_dirty) return;
            _dirty = false;

            try
            {
                var snapshot = new Dictionary<string, string>(_values, StringComparer.Ordinal);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });

                // Write-then-move so a crash mid-write cannot truncate the real file.
                var temp = _filePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _filePath, overwrite: true);
            }
            catch
            {
                _dirty = true;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _flushTimer.Dispose();
        FlushIfDirty();
    }
}

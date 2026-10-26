using Shared;

namespace ChatAppBlazor.State;

/// <summary>
/// Who is currently typing, with automatic expiry. The server sends no "stopped
/// typing" event — an indicator simply ages out — so entries carry a deadline that
/// <see cref="Sweep"/> collects.
/// </summary>
public sealed class TypingTracker
{
    private readonly Dictionary<Guid, (string Name, DateTime ExpiresAt)> _typing = [];

    /// <summary>Records a signal. Returns true when the visible set changed.</summary>
    public bool Touch(Guid userId, string userName, DateTime now)
    {
        var wasVisible = _typing.ContainsKey(userId);
        _typing[userId] = (userName, now.AddSeconds(ChatLimits.TypingExpirySeconds));
        return !wasVisible;
    }

    /// <summary>Drops expired entries. Returns true when the visible set changed.</summary>
    public bool Sweep(DateTime now)
    {
        if (_typing.Count == 0) return false;

        var stale = _typing.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToArray();
        foreach (var userId in stale)
        {
            _typing.Remove(userId);
        }

        return stale.Length > 0;
    }

    /// <summary>Clears one user — used when their message arrives, which ends the typing.</summary>
    public bool Remove(Guid userId) => _typing.Remove(userId);

    public bool Clear()
    {
        if (_typing.Count == 0) return false;
        _typing.Clear();
        return true;
    }

    public string[] Names(DateTime now) =>
        _typing.Where(kv => kv.Value.ExpiresAt > now).Select(kv => kv.Value.Name).ToArray();

    /// <summary>Human-readable summary, or null when nobody is typing.</summary>
    public string? Describe(DateTime now)
    {
        var names = Names(now);
        return names.Length switch
        {
            0 => null,
            1 => $"{names[0]} is typing…",
            2 => $"{names[0]} and {names[1]} are typing…",
            _ => "Several people are typing…"
        };
    }
}

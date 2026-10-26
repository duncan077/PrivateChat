using Shared.Models;

namespace ChatAppBlazor.State;

/// <summary>
/// Applies reaction deltas to a message's aggregated reaction set. The server sends
/// one user's change at a time, so both stores fold it into the array the same way.
/// </summary>
public static class ReactionState
{
    public static ReactionInfo[] Apply(ReactionInfo[] current, Guid userId, string emoji, bool added)
    {
        var list = current.ToList();
        var existing = list.FirstOrDefault(r => string.Equals(r.Emoji, emoji, StringComparison.Ordinal));

        if (added)
        {
            if (existing == null)
            {
                list.Add(new ReactionInfo { Emoji = emoji, UserIds = [userId] });
            }
            else if (!existing.UserIds.Contains(userId))
            {
                existing.UserIds = [.. existing.UserIds, userId];
            }
        }
        else if (existing != null)
        {
            existing.UserIds = existing.UserIds.Where(id => id != userId).ToArray();
            // An emoji nobody is left reacting with should not linger as an empty chip.
            if (existing.UserIds.Length == 0)
            {
                list.Remove(existing);
            }
        }

        return [.. list];
    }
}

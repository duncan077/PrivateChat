using Shared.Models;

namespace ChatAppBlazor.State;

/// <summary>Shared history reconciliation for rooms and DMs. Keeps loaded objects and list identity.</summary>
public static class MessageHistory
{
    public static int Merge<T>(List<T> messages, IEnumerable<T> incoming, Func<T, MessageCursor> position, HashSet<Guid>? knownIds = null)
    {
        var known = knownIds ?? messages.Select(m => position(m).Id).ToHashSet();
        var added = 0;
        foreach (var message in incoming)
        {
            var cursor = position(message);
            // A live edit/reaction may have arrived after this page was requested.
            // Existing objects are authoritative; a delayed page must not overwrite them.
            if (!known.Add(cursor.Id)) continue;
            var low = 0;
            var high = messages.Count;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (Compare(position(messages[mid]), cursor) < 0) low = mid + 1;
                else high = mid;
            }
            messages.Insert(low, message);
            added++;
        }
        return added;
    }

    public static int Compare(MessageCursor left, MessageCursor right)
    {
        var time = left.SentAt.CompareTo(right.SentAt);
        return time != 0 ? time : left.Id.CompareTo(right.Id);
    }

    public static async Task RecoverAsync<T>(MessageCursor? after,
        Func<MessageCursor?, Task<T[]>> fetch, Func<T, MessageCursor> position,
        Func<bool> isCurrent, Action<T[]> merge, int pageSize = 50)
    {
        while (isCurrent())
        {
            var page = await fetch(after);
            if (!isCurrent()) return;
            merge(page);
            // No cursor means an empty/new conversation: the first request loads its tail.
            if (after is null || page.Length < pageSize) return;
            var next = position(page[^1]);
            if (Compare(next, after) <= 0)
                throw new InvalidOperationException("History cursor did not advance.");
            after = next;
        }
    }
}

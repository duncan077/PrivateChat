using MessagePack;

namespace Shared.Models;

/// <summary>
/// Per-user unread summary for one room. Delivered on connect and after reconnect;
/// live changes are tracked client-side from message and read events.
/// </summary>
[MessagePackObject]
public class RoomUnreadState
{
    [Key(0)] public Guid RoomId { get; set; }

    /// <summary>Capped server-side — the badge shows "99+" beyond that anyway.</summary>
    [Key(1)] public int UnreadCount { get; set; }

    [Key(2)] public bool HasMention { get; set; }
    [Key(3)] public DateTime LastReadAt { get; set; }
}

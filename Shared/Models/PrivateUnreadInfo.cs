using MessagePack;

namespace Shared.Models;

/// <summary>Unread message count for one direct-message conversation.</summary>
[MessagePackObject]
public class PrivateUnreadInfo
{
    [Key(0)] public Guid PeerUserId { get; set; }
    [Key(1)] public int UnreadCount { get; set; }
}

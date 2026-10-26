using Shared.Models;

namespace ChatServer.Data.Entities;

public class ChatRoomMember
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid UserId { get; set; }
    public UserRole Role { get; set; } = UserRole.Member;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Read cursor for this member. A timestamp rather than a per-message flag: rooms
    /// fan out to everyone, so a flag would mean one row per member per message.
    /// </summary>
    public DateTime LastReadAt { get; set; } = DateTime.UtcNow;
    public ChatRoomEntity Room { get; set; } = null!;
    public AppUser User { get; set; } = null!;
}

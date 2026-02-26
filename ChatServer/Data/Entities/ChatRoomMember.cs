using Shared.Models;

namespace ChatServer.Data.Entities;

public class ChatRoomMember
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid UserId { get; set; }
    public UserRole Role { get; set; } = UserRole.Member;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public ChatRoomEntity Room { get; set; } = null!;
    public AppUser User { get; set; } = null!;
}

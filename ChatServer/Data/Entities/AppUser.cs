using Microsoft.AspNetCore.Identity;

namespace ChatServer.Data.Entities;

public class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<ChatRoomMember> RoomMemberships { get; set; } = [];
    public ICollection<MessageEntity> Messages { get; set; } = [];
    public ICollection<PrivateMessageEntity> SentPrivateMessages { get; set; } = [];
    public ICollection<PrivateMessageEntity> ReceivedPrivateMessages { get; set; } = [];
}

namespace ChatServer.Data.Entities;

public class ChatRoomEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid OwnerId { get; set; }
    public bool IsVoiceEnabled { get; set; }
    public bool IsVideoEnabled { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public AppUser Owner { get; set; } = null!;
    public ICollection<ChatRoomMember> Members { get; set; } = [];
    public ICollection<MessageEntity> Messages { get; set; } = [];
}

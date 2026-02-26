using Shared.Models;

namespace ChatServer.Data.Entities;

public class MessageEntity
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid SenderId { get; set; }
    public string Content { get; set; } = string.Empty;
    public ChatMessageType MessageType { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public string MentionedUserIdsJson { get; set; } = "[]";
    public bool IsDeleted { get; set; }
    public ChatRoomEntity Room { get; set; } = null!;
    public AppUser Sender { get; set; } = null!;
    public ICollection<AttachmentEntity> Attachments { get; set; } = [];
}

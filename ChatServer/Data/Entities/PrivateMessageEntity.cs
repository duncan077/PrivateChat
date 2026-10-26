namespace ChatServer.Data.Entities;

public class PrivateMessageEntity
{
    public Guid Id { get; set; }
    public Guid SenderId { get; set; }
    public Guid RecipientId { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public string MentionedUserIdsJson { get; set; } = "[]";
    public bool IsRead { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? EditedAt { get; set; }
    public Guid? ReplyToMessageId { get; set; }
    public PrivateMessageEntity? ReplyTo { get; set; }
    public AppUser Sender { get; set; } = null!;
    public AppUser Recipient { get; set; } = null!;
    public ICollection<AttachmentEntity> Attachments { get; set; } = [];
}

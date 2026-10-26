using MessagePack;

namespace Shared.Models;

[MessagePackObject]
public class ChatMessage
{
    [Key(0)] public Guid Id { get; set; }
    [Key(1)] public Guid RoomId { get; set; }
    [Key(2)] public Guid SenderId { get; set; }
    [Key(3)] public string SenderName { get; set; } = string.Empty;
    [Key(4)] public string Content { get; set; } = string.Empty;
    [Key(5)] public ChatMessageType MessageType { get; set; }
    [Key(6)] public DateTime SentAt { get; set; }
    [Key(7)] public Guid[] MentionedUserIds { get; set; } = [];
    [Key(8)] public AttachmentInfo[] Attachments { get; set; } = [];
    [Key(9)] public bool IsDeleted { get; set; }

    // Appended keys only — reordering breaks deployed clients.
    [Key(10)] public DateTime? EditedAt { get; set; }
    [Key(11)] public ReactionInfo[] Reactions { get; set; } = [];
    [Key(12)] public Guid? ReplyToMessageId { get; set; }
    [Key(13)] public MessageReplyPreview? ReplyPreview { get; set; }
}

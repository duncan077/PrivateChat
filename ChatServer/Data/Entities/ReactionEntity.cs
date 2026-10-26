namespace ChatServer.Data.Entities;

/// <summary>
/// A single user's reaction to one message. Parent is either a room message or a
/// private message — the same nullable-dual-FK shape AttachmentEntity uses.
/// </summary>
public class ReactionEntity
{
    public Guid Id { get; set; }
    public Guid? MessageId { get; set; }
    public Guid? PrivateMessageId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>
    /// Unicode sequence or <c>:name:</c> custom token. Stored under utf8mb4_bin —
    /// see AppDbContext for why the collation matters here.
    /// </summary>
    public string Emoji { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MessageEntity? Message { get; set; }
    public PrivateMessageEntity? PrivateMessage { get; set; }
}

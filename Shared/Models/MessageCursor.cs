using MessagePack;

namespace Shared.Models;

/// <summary>A unique position in a conversation, ordered by timestamp then message id.</summary>
[MessagePackObject]
public sealed class MessageCursor
{
    [Key(0)] public DateTime SentAt { get; set; }
    [Key(1)] public Guid Id { get; set; }
}

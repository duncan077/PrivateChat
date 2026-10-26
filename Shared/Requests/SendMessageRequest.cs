using MessagePack;
using Shared.Models;

namespace Shared.Requests;

[MessagePackObject]
public class SendMessageRequest
{
    [Key(0)] public string Content { get; set; } = string.Empty;
    [Key(1)] public Guid[] MentionedUserIds { get; set; } = [];
    [Key(2)] public AttachmentInfo[] Attachments { get; set; } = [];

    /// <summary>
    /// Appended key. A server predating this field skips it, so a reply sent to an old
    /// server silently becomes a plain message — deploy the server before clients.
    /// </summary>
    [Key(3)] public Guid? ReplyToMessageId { get; set; }
}

using MessagePack;
using Shared.Models;

namespace Shared.Requests;

[MessagePackObject]
public class SendMessageRequest
{
    [Key(0)] public string Content { get; set; } = string.Empty;
    [Key(1)] public Guid[] MentionedUserIds { get; set; } = [];
    [Key(2)] public AttachmentInfo[] Attachments { get; set; } = [];
}

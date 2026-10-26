using MessagePack;

namespace Shared.Models;

/// <summary>
/// The quoted snippet shown above a reply. Denormalized at read time from the parent
/// rather than stored, so an edited parent's quote stays truthful.
/// </summary>
[MessagePackObject]
public class MessageReplyPreview
{
    [Key(0)] public string SenderName { get; set; } = string.Empty;

    /// <summary>Leading characters of the parent's raw content, not rendered markdown.</summary>
    [Key(1)] public string Snippet { get; set; } = string.Empty;

    /// <summary>
    /// True when the parent was deleted or is otherwise unreachable. Deleted messages
    /// are filtered out of history entirely, so the reply has to say so itself.
    /// </summary>
    [Key(2)] public bool IsUnavailable { get; set; }
}

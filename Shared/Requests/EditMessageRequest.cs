using MessagePack;

namespace Shared.Requests;

[MessagePackObject]
public class EditMessageRequest
{
    [Key(0)] public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Mention set re-extracted from the edited content. Replaces the stored set;
    /// no new mention notifications are raised for edits.
    /// </summary>
    [Key(1)] public Guid[] MentionedUserIds { get; set; } = [];
}

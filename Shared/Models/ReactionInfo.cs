using MessagePack;

namespace Shared.Models;

/// <summary>
/// One emoji on a message, aggregated across everyone who reacted with it.
/// Count and "did I react" are derived client-side from <see cref="UserIds"/>,
/// which also feeds the who-reacted tooltip. Rooms cap at ~20 people, so
/// carrying the ids costs little.
/// </summary>
[MessagePackObject]
public class ReactionInfo
{
    /// <summary>
    /// A unicode emoji sequence, or a custom-emoji token of the form <c>:name:</c>.
    /// </summary>
    [Key(0)] public string Emoji { get; set; } = string.Empty;

    [Key(1)] public Guid[] UserIds { get; set; } = [];
}

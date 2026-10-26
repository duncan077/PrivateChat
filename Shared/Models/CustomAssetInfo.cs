using MessagePack;

namespace Shared.Models;

public enum CustomAssetKind
{
    Emoji = 0,
    Sticker = 1
}

/// <summary>
/// A server-wide custom emoji or sticker. Emoji are referenced in message text and in
/// reactions as <c>:name:</c> tokens; stickers are sent as ordinary image attachments.
/// </summary>
[MessagePackObject]
public class CustomAssetInfo
{
    [Key(0)] public Guid Id { get; set; }
    [Key(1)] public CustomAssetKind Kind { get; set; }

    /// <summary>Lowercase <c>[a-z0-9_]</c>, unique per kind.</summary>
    [Key(2)] public string Name { get; set; } = string.Empty;

    [Key(3)] public string Url { get; set; } = string.Empty;
    [Key(4)] public Guid UploaderId { get; set; }
}

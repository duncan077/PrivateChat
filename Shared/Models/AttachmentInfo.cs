using MessagePack;

namespace Shared.Models;

[MessagePackObject]
public class AttachmentInfo
{
    [Key(0)] public Guid Id { get; set; }
    [Key(1)] public string FileName { get; set; } = string.Empty;
    [Key(2)] public string ContentType { get; set; } = string.Empty;
    [Key(3)] public long SizeBytes { get; set; }
    [Key(4)] public string Url { get; set; } = string.Empty;
}

using MessagePack;

namespace Shared.Models;

[MessagePackObject]
public class ChatRoomInfo
{
    [Key(0)] public Guid Id { get; set; }
    [Key(1)] public string Name { get; set; } = string.Empty;
    [Key(2)] public string? Description { get; set; }
    [Key(3)] public Guid OwnerId { get; set; }
    [Key(4)] public int MemberCount { get; set; }
    [Key(5)] public bool IsVoiceEnabled { get; set; }
    [Key(6)] public bool IsVideoEnabled { get; set; }
    [Key(7)] public DateTime CreatedAt { get; set; }
}

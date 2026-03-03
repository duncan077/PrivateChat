using MessagePack;

namespace Shared.Models;

[MessagePackObject]
public class ServerMemberInfo
{
    [Key(0)] public Guid UserId { get; set; }
    [Key(1)] public string UserName { get; set; } = string.Empty;
    [Key(2)] public string DisplayName { get; set; } = string.Empty;
    [Key(3)] public string? AvatarUrl { get; set; }
    [Key(4)] public bool IsOnline { get; set; }
    [Key(5)] public Guid[] RoomIds { get; set; } = [];
    [Key(6)] public UserRole Role { get; set; }
}

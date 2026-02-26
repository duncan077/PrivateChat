using MessagePack;

namespace Shared.Models;

[MessagePackObject]
public class ChatMemberInfo
{
    [Key(0)] public Guid UserId { get; set; }
    [Key(1)] public string UserName { get; set; } = string.Empty;
    [Key(2)] public string DisplayName { get; set; } = string.Empty;
    [Key(3)] public string? AvatarUrl { get; set; }
    [Key(4)] public UserRole Role { get; set; }
    [Key(5)] public DateTime JoinedAt { get; set; }
    [Key(6)] public bool IsOnline { get; set; }
}

using MessagePack;

namespace Shared.Models;

[MessagePackObject]
public class UserInfo
{
    [Key(0)] public Guid Id { get; set; }
    [Key(1)] public string UserName { get; set; } = string.Empty;
    [Key(2)] public string DisplayName { get; set; } = string.Empty;
    [Key(3)] public string? AvatarUrl { get; set; }
    [Key(4)] public bool IsOnline { get; set; }
}

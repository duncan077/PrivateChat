using MessagePack;

namespace Shared.Models;

[MessagePackObject]
public class UserProfileUpdateInfo
{
    [Key(0)] public Guid UserId { get; set; }
    [Key(1)] public string DisplayName { get; set; } = string.Empty;
    [Key(2)] public string? AvatarUrl { get; set; }
}

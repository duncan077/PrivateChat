using MessagePack;

namespace Shared.Requests;

[MessagePackObject]
public class UpdateProfileRequest
{
    [Key(0)] public string DisplayName { get; set; } = string.Empty;
    [Key(1)] public string? AvatarUrl { get; set; }
}

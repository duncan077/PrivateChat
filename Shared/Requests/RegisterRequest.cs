using MessagePack;

namespace Shared.Requests;

[MessagePackObject]
public class RegisterRequest
{
    [Key(0)] public string UserName { get; set; } = string.Empty;
    [Key(1)] public string DisplayName { get; set; } = string.Empty;
    [Key(2)] public string Password { get; set; } = string.Empty;
}

using MessagePack;
using Shared.Models;

namespace Shared.Responses;

[MessagePackObject]
public class AuthResponse
{
    [Key(0)] public bool Success { get; set; }
    [Key(1)] public string? Token { get; set; }
    [Key(2)] public UserInfo? User { get; set; }
    [Key(3)] public string? Error { get; set; }
}

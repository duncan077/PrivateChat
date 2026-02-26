using MessagePack;

namespace Shared.Requests;

[MessagePackObject]
public class CreateRoomRequest
{
    [Key(0)] public string Name { get; set; } = string.Empty;
    [Key(1)] public string? Description { get; set; }
    [Key(2)] public bool IsVoiceEnabled { get; set; }
    [Key(3)] public bool IsVideoEnabled { get; set; }
}

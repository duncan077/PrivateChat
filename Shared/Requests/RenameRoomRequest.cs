using MessagePack;

namespace Shared.Requests;

[MessagePackObject]
public class RenameRoomRequest
{
    [Key(0)] public Guid RoomId { get; set; }
    [Key(1)] public string Name { get; set; } = string.Empty;
}

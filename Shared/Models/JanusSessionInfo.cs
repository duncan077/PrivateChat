using MessagePack;

namespace Shared.Models;

[MessagePackObject]
public class JanusSessionInfo
{
    [Key(0)] public Guid RoomId { get; set; }
    [Key(1)] public long JanusSessionId { get; set; }
    [Key(2)] public long JanusHandleId { get; set; }
    [Key(3)] public string JanusUrl { get; set; } = string.Empty;
    [Key(4)] public string IceServers { get; set; } = string.Empty;
    [Key(5)] public string ServerBaseUrl { get; set; } = string.Empty;
    [Key(6)] public long JanusRoomId { get; set; }
}

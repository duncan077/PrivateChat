using MessagePack;

namespace Shared.Models;

[MessagePackObject]
public class RoomCallParticipantsInfo
{
    [Key(0)] public Guid RoomId { get; set; }
    [Key(1)] public Guid[] ParticipantUserIds { get; set; } = [];
}

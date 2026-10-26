using MessagePack;

namespace Shared.Models;

/// <summary>A search hit plus the room it lives in, so results from several rooms are distinguishable.</summary>
[MessagePackObject]
public class SearchResultInfo
{
    [Key(0)] public ChatMessage Message { get; set; } = new();
    [Key(1)] public string RoomName { get; set; } = string.Empty;
}

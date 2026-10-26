using MessagePack;

namespace Shared.Requests;

[MessagePackObject]
public class SearchMessagesRequest
{
    [Key(0)] public string Query { get; set; } = string.Empty;

    /// <summary>Null searches every room the caller belongs to.</summary>
    [Key(1)] public Guid? RoomId { get; set; }

    /// <summary>Exclusive cursor for paging, newest-first — same shape as history paging.</summary>
    [Key(2)] public DateTime? Before { get; set; }

    [Key(3)] public int Count { get; set; }
}

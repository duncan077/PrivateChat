using MagicOnion;
using Shared.Models;
using Shared.Requests;

namespace Shared.Services;

public interface IChatRoomService : IService<IChatRoomService>
{
    UnaryResult<ChatRoomInfo[]> GetRoomsAsync();
    UnaryResult<ChatRoomInfo> GetRoomAsync(Guid roomId);
    UnaryResult<ChatRoomInfo> CreateRoomAsync(CreateRoomRequest request);
    UnaryResult<bool> DeleteRoomAsync(Guid roomId);
    UnaryResult<ChatRoomInfo> RenameRoomAsync(RenameRoomRequest request);
    UnaryResult<ServerMemberInfo[]> GetServerMembersAsync();
    UnaryResult<RoomCallParticipantsInfo[]> GetRoomCallParticipantsAsync();
    UnaryResult<ChatMemberInfo[]> GetRoomMembersAsync(Guid roomId);
    UnaryResult<ChatMessage[]> GetRoomMessagesAsync(Guid roomId, int count, DateTime? before);

    /// <summary>Before pages are newest-first; after pages are oldest-first. Use only one cursor.</summary>
    UnaryResult<ChatMessage[]> GetRoomMessagePageAsync(Guid roomId, int count, MessageCursor? before, MessageCursor? after);

    /// <summary>
    /// A window of messages centered on one message, oldest-first. Backs "jump to the
    /// quoted message" when the parent is not in the loaded page.
    /// </summary>
    UnaryResult<ChatMessage[]> GetRoomMessagesAroundAsync(Guid roomId, Guid messageId, int contextCount);

    /// <summary>
    /// Unread summary for every room the caller belongs to. Deliberately its own call
    /// rather than a field on ChatRoomInfo, which is cached globally across users.
    /// </summary>
    UnaryResult<RoomUnreadState[]> GetRoomUnreadStatesAsync();

    /// <summary>Searches message history in rooms the caller belongs to, newest first.</summary>
    UnaryResult<SearchResultInfo[]> SearchMessagesAsync(SearchMessagesRequest request);
}

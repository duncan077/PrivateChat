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
    UnaryResult<ChatMemberInfo[]> GetRoomMembersAsync(Guid roomId);
    UnaryResult<ChatMessage[]> GetRoomMessagesAsync(Guid roomId, int count, DateTime? before);
}

using MagicOnion;
using Shared.Models;

namespace Shared.Services;

public interface IPrivateMessageService : IService<IPrivateMessageService>
{
    UnaryResult<UserInfo[]> GetContactsAsync();
    UnaryResult<PrivateMessage[]> GetMessagesAsync(Guid otherUserId, int count, DateTime? before);

    /// <summary>Before pages are newest-first; after pages are oldest-first. Use only one cursor.</summary>
    UnaryResult<PrivateMessage[]> GetMessagePageAsync(Guid otherUserId, int count, MessageCursor? before, MessageCursor? after);

    /// <summary>Unread counts per conversation, for badges on connect and reconnect.</summary>
    UnaryResult<PrivateUnreadInfo[]> GetUnreadCountsAsync();

    /// <summary>Searches the caller's own conversations, newest first.</summary>
    UnaryResult<PrivateMessage[]> SearchMessagesAsync(string query, int count, DateTime? before);
}

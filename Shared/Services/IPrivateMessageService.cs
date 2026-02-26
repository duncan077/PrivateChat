using MagicOnion;
using Shared.Models;

namespace Shared.Services;

public interface IPrivateMessageService : IService<IPrivateMessageService>
{
    UnaryResult<UserInfo[]> GetContactsAsync();
    UnaryResult<PrivateMessage[]> GetMessagesAsync(Guid otherUserId, int count, DateTime? before);
}

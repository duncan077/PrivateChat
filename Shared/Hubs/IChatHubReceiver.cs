using Shared.Models;

namespace Shared.Hubs;

public interface IChatHubReceiver
{
    void OnMessageReceived(ChatMessage message);
    void OnMessageDeleted(Guid roomId, Guid messageId);
    void OnRoomDeleted(Guid roomId);
    void OnUserJoinedRoom(Guid roomId, ChatMemberInfo member);
    void OnUserLeftRoom(Guid roomId, Guid userId);
    void OnUserKicked(Guid roomId, Guid userId, string reason);
    void OnUserKickedFromVoiceVideo(Guid roomId, Guid userId, string reason);
    void OnUserMoved(Guid sourceRoomId, Guid targetRoomId, Guid userId);
    void OnUserRoleChanged(Guid roomId, Guid userId, UserRole newRole);
    void OnPrivateMessageReceived(PrivateMessage message);
    void OnPrivateMessagesRead(Guid userId);
    void OnUserOnlineStatusChanged(Guid userId, bool isOnline);
    void OnWebRtcSignalReceived(Guid roomId, Guid fromUserId, string signalType, string payload);
    void OnUserJoinedVoiceVideo(Guid roomId, Guid userId);
    void OnUserLeftVoiceVideo(Guid roomId, Guid userId);
}

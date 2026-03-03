using MagicOnion;
using Shared.Models;
using Shared.Requests;

namespace Shared.Hubs;

public interface IChatHub : IStreamingHub<IChatHub, IChatHubReceiver>
{
    ValueTask JoinRoomAsync(Guid roomId);
    ValueTask LeaveRoomAsync(Guid roomId);
    ValueTask DeleteRoomAsync(Guid roomId);
    ValueTask SendMessageAsync(Guid roomId, SendMessageRequest request);
    ValueTask DeleteMessageAsync(Guid roomId, Guid messageId);
    ValueTask KickUserAsync(Guid roomId, Guid userId);
    ValueTask KickUserFromVoiceVideoAsync(Guid roomId, Guid userId, string reason);
    ValueTask MoveUserAsync(Guid sourceRoomId, Guid targetRoomId, Guid userId);
    ValueTask SetUserRoleAsync(Guid roomId, Guid userId, UserRole role);
    ValueTask SendPrivateMessageAsync(Guid recipientId, SendMessageRequest request);
    ValueTask MarkPrivateMessageReadAsync(Guid senderId);
    ValueTask<JanusSessionInfo> JoinVoiceVideoAsync(Guid roomId);
    ValueTask LeaveVoiceVideoAsync(Guid roomId);
    ValueTask<Guid[]> GetVoiceVideoParticipantsAsync(Guid roomId);
    ValueTask SendWebRtcSignalAsync(Guid roomId, Guid targetUserId, string signalType, string payload);
}

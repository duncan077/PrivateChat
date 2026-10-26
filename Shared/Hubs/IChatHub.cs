using MagicOnion;
using Shared.Models;
using Shared.Requests;

namespace Shared.Hubs;

public interface IChatHub : IStreamingHub<IChatHub, IChatHubReceiver>
{
    ValueTask JoinRoomAsync(Guid roomId);
    ValueTask LeaveRoomAsync(Guid roomId);
    ValueTask DeleteRoomAsync(Guid roomId);
    ValueTask RenameRoomAsync(Guid roomId, string name);
    ValueTask SendMessageAsync(Guid roomId, SendMessageRequest request);
    ValueTask DeleteMessageAsync(Guid roomId, Guid messageId);

    /// <summary>Sender-only; moderators can delete but not edit others' messages.</summary>
    ValueTask EditMessageAsync(Guid roomId, Guid messageId, EditMessageRequest request);

    /// <summary>Adds the caller's reaction, or removes it if already present.</summary>
    ValueTask ToggleReactionAsync(Guid roomId, Guid messageId, string emoji);

    /// <summary>
    /// Announces that the caller is composing. Ephemeral — nothing is persisted and
    /// there is no matching "stopped typing"; receivers expire the indicator instead.
    /// </summary>
    ValueTask SetTypingAsync(Guid roomId);

    /// <summary>
    /// Advances the caller's read cursor for a room. Monotonic — an older timestamp is
    /// ignored, so out-of-order calls from several devices cannot un-read a room.
    /// </summary>
    ValueTask MarkRoomReadAsync(Guid roomId, DateTime lastReadAt);
    ValueTask KickUserAsync(Guid roomId, Guid userId);
    ValueTask KickUserFromVoiceVideoAsync(Guid roomId, Guid userId, string reason);
    ValueTask MoveUserAsync(Guid sourceRoomId, Guid targetRoomId, Guid userId);
    ValueTask SetUserRoleAsync(Guid roomId, Guid userId, UserRole role);
    ValueTask<UserInfo> UpdateProfileAsync(UpdateProfileRequest request);
    ValueTask SendPrivateMessageAsync(Guid recipientId, SendMessageRequest request);
    ValueTask MarkPrivateMessageReadAsync(Guid senderId);

    // DM moderation is sender-only; the peer is derived from the message row, so
    // these take just the message id.
    ValueTask DeletePrivateMessageAsync(Guid messageId);
    ValueTask EditPrivateMessageAsync(Guid messageId, EditMessageRequest request);
    ValueTask TogglePrivateReactionAsync(Guid messageId, string emoji);
    ValueTask SetPrivateTypingAsync(Guid peerUserId);
    ValueTask<JanusSessionInfo> JoinVoiceVideoAsync(Guid roomId);
    ValueTask LeaveVoiceVideoAsync(Guid roomId);
    ValueTask EnsureVoiceVideoPresenceAsync(Guid roomId);
    ValueTask<Guid[]> GetVoiceVideoParticipantsAsync(Guid roomId);
    ValueTask SendWebRtcSignalAsync(Guid roomId, Guid targetUserId, string signalType, string payload);
    ValueTask<JanusSessionInfo> JoinPrivateVoiceVideoAsync(Guid peerUserId);
    ValueTask LeavePrivateVoiceVideoAsync(Guid peerUserId);
    ValueTask EnsurePrivateVoiceVideoPresenceAsync(Guid peerUserId);
    ValueTask<Guid[]> GetPrivateVoiceVideoParticipantsAsync(Guid peerUserId);
    ValueTask SendPrivateWebRtcSignalAsync(Guid peerUserId, Guid targetUserId, string signalType, string payload);

    // ─── Screen-share annotations ──────────────────────────────────────────────
    //
    // A call can have several shares running at once, each with its own annotation
    // session owned by whoever is sharing. Set/Send act on the caller's own session;
    // the Get calls return every session currently active in the call.
    ValueTask SetScreenAnnotationStateAsync(Guid roomId, ScreenAnnotationState state);
    ValueTask SendScreenAnnotationAsync(Guid roomId, ScreenAnnotationEvent annotationEvent);
    ValueTask<ScreenAnnotationState[]> GetScreenAnnotationSessionsAsync(Guid roomId);
    ValueTask SetPrivateScreenAnnotationStateAsync(Guid peerUserId, ScreenAnnotationState state);
    ValueTask SendPrivateScreenAnnotationAsync(Guid peerUserId, ScreenAnnotationEvent annotationEvent);
    ValueTask<ScreenAnnotationState[]> GetPrivateScreenAnnotationSessionsAsync(Guid peerUserId);
}

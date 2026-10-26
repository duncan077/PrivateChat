using Shared.Models;

namespace Shared.Hubs;

public interface IChatHubReceiver
{
    void OnMessageReceived(ChatMessage message);
    void OnMessageDeleted(Guid roomId, Guid messageId);
    void OnMessageEdited(Guid roomId, Guid messageId, string content, Guid[] mentionedUserIds, DateTime editedAt);

    /// <summary>One person's reaction changed — a delta, not the message's whole set.</summary>
    void OnMessageReactionChanged(Guid roomId, Guid messageId, Guid userId, string emoji, bool added);

    /// <summary>Someone is composing. Receivers expire this on their own; there is no stop event.</summary>
    void OnTyping(Guid roomId, Guid userId, string userName);

    /// <summary>
    /// The caller read a room — sent only to that user's own connections, so their
    /// other devices can clear the badge too.
    /// </summary>
    void OnRoomRead(Guid roomId, DateTime lastReadAt);
    void OnRoomDeleted(Guid roomId);
    void OnRoomRenamed(Guid roomId, string newName);
    void OnUserJoinedRoom(Guid roomId, ChatMemberInfo member);
    void OnUserLeftRoom(Guid roomId, Guid userId);
    void OnUserKicked(Guid roomId, Guid userId, string reason);
    void OnUserKickedFromVoiceVideo(Guid roomId, Guid userId, string reason);
    void OnUserMoved(Guid sourceRoomId, Guid targetRoomId, Guid userId);
    void OnUserRoleChanged(Guid roomId, Guid userId, UserRole newRole);
    void OnUserProfileUpdated(UserProfileUpdateInfo profile);
    void OnPrivateMessageReceived(PrivateMessage message);
    void OnPrivateMessagesRead(Guid userId);

    // DM deltas carry both participant ids so every one of either party's devices
    // can locate the conversation without a lookup.
    void OnPrivateMessageDeleted(Guid senderId, Guid recipientId, Guid messageId);
    void OnPrivateMessageEdited(Guid senderId, Guid recipientId, Guid messageId, string content, DateTime editedAt);
    void OnPrivateMessageReactionChanged(Guid senderId, Guid recipientId, Guid messageId, Guid userId, string emoji, bool added);
    void OnPrivateTyping(Guid userId, string userName);

    /// <summary>
    /// Names both sides of a conversation that was just read. OnPrivateMessagesRead
    /// carries only the reader, so the reader's other devices cannot tell which
    /// conversation to clear; this exists alongside it rather than replacing it,
    /// because widening the old event would break already-deployed clients.
    /// </summary>
    void OnPrivateConversationRead(Guid readerUserId, Guid peerUserId);
    void OnUserOnlineStatusChanged(Guid userId, bool isOnline);
    void OnWebRtcSignalReceived(Guid roomId, Guid fromUserId, string signalType, string payload);
    void OnUserJoinedVoiceVideo(Guid roomId, Guid userId);
    void OnUserLeftVoiceVideo(Guid roomId, Guid userId);
    void OnPrivateCallIncoming(Guid callerUserId);

    /// <summary>
    /// Annotation permission/capture state changed for <em>one share</em> in a call.
    /// <paramref name="contextId"/> is the room id, or the private call context id for DM calls.
    ///
    /// This is a per-sharer delta, not the whole call: <c>state.SharerUserId</c> says
    /// which share it describes, and <c>state.Enabled == false</c> means that sharer's
    /// session ended. Other concurrent sessions are unaffected.
    /// </summary>
    void OnScreenAnnotationStateChanged(Guid contextId, ScreenAnnotationState state);

    /// <summary>
    /// A participant drew on (or cleared) a shared screen. Which screen is carried by
    /// <c>annotationEvent.TargetSharerUserId</c>.
    /// </summary>
    void OnScreenAnnotationReceived(Guid contextId, Guid fromUserId, ScreenAnnotationEvent annotationEvent);
}

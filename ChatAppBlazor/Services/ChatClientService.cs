using MagicOnion.Client;
using Shared.Hubs;
using Shared.Models;
using Shared.Requests;

namespace ChatAppBlazor.Services;

public class ChatClientService : IChatHubReceiver, IAsyncDisposable
{
    private readonly GrpcChannelProvider _channelProvider;
    private IChatHub? _hub;
    private bool _intentionalDisconnect;
    private CancellationTokenSource? _reconnectCts;

    public event Action<ChatMessage>? OnMessageReceived;
    public event Action<Guid, Guid>? OnMessageDeleted;
    public event Action<Guid, Guid, string, Guid[], DateTime>? OnMessageEdited;
    public event Action<Guid, Guid, Guid, string, bool>? OnMessageReactionChanged;
    public event Action<Guid, Guid, string>? OnTyping;
    public event Action<Guid, DateTime>? OnRoomRead;
    public event Action<Guid>? OnRoomDeleted;
    public event Action<Guid, string>? OnRoomRenamed;
    public event Action<Guid, ChatMemberInfo>? OnUserJoinedRoom;
    public event Action<Guid, Guid>? OnUserLeftRoom;
    public event Action<Guid, Guid, string>? OnUserKicked;
    public event Action<Guid, Guid, string>? OnUserKickedFromVoiceVideo;
    public event Action<Guid, Guid, Guid>? OnUserMoved;
    public event Action<Guid, Guid, UserRole>? OnUserRoleChanged;
    public event Action<UserProfileUpdateInfo>? OnUserProfileUpdated;
    public event Action<PrivateMessage>? OnPrivateMessageReceived;
    public event Action<Guid>? OnPrivateMessagesRead;
    public event Action<Guid, Guid, Guid>? OnPrivateMessageDeleted;
    public event Action<Guid, Guid, Guid, string, DateTime>? OnPrivateMessageEdited;
    public event Action<Guid, Guid, Guid, Guid, string, bool>? OnPrivateMessageReactionChanged;
    public event Action<Guid, string>? OnPrivateTyping;
    public event Action<Guid, Guid>? OnPrivateConversationRead;
    public event Action<Guid, bool>? OnUserOnlineStatusChanged;
    public event Action<Guid, Guid, string, string>? OnWebRtcSignalReceived;
    public event Action<Guid, Guid>? OnUserJoinedVoiceVideo;
    public event Action<Guid, Guid>? OnUserLeftVoiceVideo;
    public event Action<Guid>? OnPrivateCallIncoming;
    public event Action<Guid, ScreenAnnotationState>? OnScreenAnnotationStateChanged;
    public event Action<Guid, Guid, ScreenAnnotationEvent>? OnScreenAnnotationReceived;

    /// <summary>
    /// Fired when connection state changes. True = connected, False = disconnected/reconnecting.
    /// </summary>
    public event Action<bool>? OnConnectionStateChanged;

    public bool IsConnected => _hub != null;

    public ChatClientService(GrpcChannelProvider channelProvider)
    {
        _channelProvider = channelProvider;
    }

    public async Task ConnectAsync()
    {
        if (_hub != null) return;

        _intentionalDisconnect = false;
        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();

        var channel = _channelProvider.GetChannel();
        _hub = await StreamingHubClient.ConnectAsync<IChatHub, IChatHubReceiver>(
            channel, this);

        OnConnectionStateChanged?.Invoke(true);

        // Start watching for disconnection in the background
        _ = WatchForDisconnect(_reconnectCts.Token);
    }

    public async Task DisconnectAsync()
    {
        _intentionalDisconnect = true;
        _reconnectCts?.Cancel();
        _reconnectCts = null;

        if (_hub != null)
        {
            await _hub.DisposeAsync();
            _hub = null;
        }

        OnConnectionStateChanged?.Invoke(false);
    }

    /// <summary>
    /// Monitors the hub connection and auto-reconnects on unexpected disconnection
    /// with exponential backoff (1s → 2s → 4s → ... max 30s).
    /// </summary>
    private async Task WatchForDisconnect(CancellationToken ct)
    {
        try
        {
            if (_hub == null) return;

            // Wait until the hub disconnects
            await _hub.WaitForDisconnect();

            // If we intentionally disconnected, don't reconnect
            if (_intentionalDisconnect || ct.IsCancellationRequested) return;

            Console.WriteLine("[ChatClient] Disconnected unexpectedly, starting reconnection...");
            _hub = null;
            OnConnectionStateChanged?.Invoke(false);

            // Exponential backoff reconnection loop
            int delayMs = 1000;
            const int maxDelayMs = 30_000;

            while (!ct.IsCancellationRequested && !_intentionalDisconnect)
            {
                try
                {
                    Console.WriteLine($"[ChatClient] Reconnecting in {delayMs}ms...");
                    await Task.Delay(delayMs, ct);

                    // Reset the gRPC channel to force a fresh connection
                    _channelProvider.ResetChannel();

                    var channel = _channelProvider.GetChannel();
                    _hub = await StreamingHubClient.ConnectAsync<IChatHub, IChatHubReceiver>(
                        channel, this);

                    Console.WriteLine("[ChatClient] Reconnected successfully!");
                    OnConnectionStateChanged?.Invoke(true);

                    // Restart disconnect watcher for the new connection
                    _ = WatchForDisconnect(ct);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ChatClient] Reconnect failed: {ex.Message}");
                    delayMs = Math.Min(delayMs * 2, maxDelayMs);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatClient] WatchForDisconnect error: {ex.Message}");
        }
    }

    public async Task JoinRoomAsync(Guid roomId)
    {
        EnsureConnected();
        await _hub!.JoinRoomAsync(roomId);
    }

    public async Task LeaveRoomAsync(Guid roomId)
    {
        EnsureConnected();
        await _hub!.LeaveRoomAsync(roomId);
    }

    public async Task DeleteRoomAsync(Guid roomId)
    {
        EnsureConnected();
        await _hub!.DeleteRoomAsync(roomId);
    }

    public async Task RenameRoomAsync(Guid roomId, string name)
    {
        EnsureConnected();
        await _hub!.RenameRoomAsync(roomId, name);
    }

    public async Task SendMessageAsync(Guid roomId, string content, Guid[]? mentionedUserIds = null, AttachmentInfo[]? attachments = null, Guid? replyToMessageId = null)
    {
        EnsureConnected();
        await _hub!.SendMessageAsync(roomId, new SendMessageRequest
        {
            Content = content,
            MentionedUserIds = mentionedUserIds ?? [],
            Attachments = attachments ?? [],
            ReplyToMessageId = replyToMessageId
        });
    }

    public async Task DeleteMessageAsync(Guid roomId, Guid messageId)
    {
        EnsureConnected();
        await _hub!.DeleteMessageAsync(roomId, messageId);
    }

    public async Task MarkRoomReadAsync(Guid roomId, DateTime lastReadAt)
    {
        EnsureConnected();
        await _hub!.MarkRoomReadAsync(roomId, lastReadAt);
    }

    public async Task SetTypingAsync(Guid roomId)
    {
        EnsureConnected();
        await _hub!.SetTypingAsync(roomId);
    }

    public async Task SetPrivateTypingAsync(Guid peerUserId)
    {
        EnsureConnected();
        await _hub!.SetPrivateTypingAsync(peerUserId);
    }

    public async Task ToggleReactionAsync(Guid roomId, Guid messageId, string emoji)
    {
        EnsureConnected();
        await _hub!.ToggleReactionAsync(roomId, messageId, emoji);
    }

    public async Task TogglePrivateReactionAsync(Guid messageId, string emoji)
    {
        EnsureConnected();
        await _hub!.TogglePrivateReactionAsync(messageId, emoji);
    }

    public async Task EditMessageAsync(Guid roomId, Guid messageId, string content, Guid[]? mentionedUserIds = null)
    {
        EnsureConnected();
        await _hub!.EditMessageAsync(roomId, messageId, new EditMessageRequest
        {
            Content = content,
            MentionedUserIds = mentionedUserIds ?? []
        });
    }

    public async Task KickUserAsync(Guid roomId, Guid userId)
    {
        EnsureConnected();
        await _hub!.KickUserAsync(roomId, userId);
    }

    public async Task KickUserFromVoiceVideoAsync(Guid roomId, Guid userId, string reason)
    {
        EnsureConnected();
        await _hub!.KickUserFromVoiceVideoAsync(roomId, userId, reason);
    }

    public async Task MoveUserAsync(Guid sourceRoomId, Guid targetRoomId, Guid userId)
    {
        EnsureConnected();
        await _hub!.MoveUserAsync(sourceRoomId, targetRoomId, userId);
    }

    public async Task SetUserRoleAsync(Guid roomId, Guid userId, UserRole role)
    {
        EnsureConnected();
        await _hub!.SetUserRoleAsync(roomId, userId, role);
    }

    public async Task<UserInfo> UpdateProfileAsync(UpdateProfileRequest request)
    {
        EnsureConnected();
        return await _hub!.UpdateProfileAsync(request);
    }

    public async Task SendPrivateMessageAsync(Guid recipientId, string content, Guid[]? mentionedUserIds = null, AttachmentInfo[]? attachments = null, Guid? replyToMessageId = null)
    {
        EnsureConnected();
        await _hub!.SendPrivateMessageAsync(recipientId, new SendMessageRequest
        {
            Content = content,
            MentionedUserIds = mentionedUserIds ?? [],
            Attachments = attachments ?? [],
            ReplyToMessageId = replyToMessageId
        });
    }

    public async Task MarkPrivateMessageReadAsync(Guid senderId)
    {
        EnsureConnected();
        await _hub!.MarkPrivateMessageReadAsync(senderId);
    }

    public async Task DeletePrivateMessageAsync(Guid messageId)
    {
        EnsureConnected();
        await _hub!.DeletePrivateMessageAsync(messageId);
    }

    public async Task EditPrivateMessageAsync(Guid messageId, string content, Guid[]? mentionedUserIds = null)
    {
        EnsureConnected();
        await _hub!.EditPrivateMessageAsync(messageId, new EditMessageRequest
        {
            Content = content,
            MentionedUserIds = mentionedUserIds ?? []
        });
    }

    public async Task<JanusSessionInfo> JoinVoiceVideoAsync(Guid roomId)
    {
        EnsureConnected();
        return await _hub!.JoinVoiceVideoAsync(roomId);
    }

    public async Task LeaveVoiceVideoAsync(Guid roomId)
    {
        EnsureConnected();
        await _hub!.LeaveVoiceVideoAsync(roomId);
    }

    public async Task EnsureVoiceVideoPresenceAsync(Guid roomId)
    {
        EnsureConnected();
        await _hub!.EnsureVoiceVideoPresenceAsync(roomId);
    }

    public async Task<Guid[]> GetVoiceVideoParticipantsAsync(Guid roomId)
    {
        EnsureConnected();
        return await _hub!.GetVoiceVideoParticipantsAsync(roomId);
    }

    public async Task SendWebRtcSignalAsync(Guid roomId, Guid targetUserId, string signalType, string payload)
    {
        EnsureConnected();
        await _hub!.SendWebRtcSignalAsync(roomId, targetUserId, signalType, payload);
    }

    public async Task<JanusSessionInfo> JoinPrivateVoiceVideoAsync(Guid peerUserId)
    {
        EnsureConnected();
        return await _hub!.JoinPrivateVoiceVideoAsync(peerUserId);
    }

    public async Task LeavePrivateVoiceVideoAsync(Guid peerUserId)
    {
        EnsureConnected();
        await _hub!.LeavePrivateVoiceVideoAsync(peerUserId);
    }

    public async Task EnsurePrivateVoiceVideoPresenceAsync(Guid peerUserId)
    {
        EnsureConnected();
        await _hub!.EnsurePrivateVoiceVideoPresenceAsync(peerUserId);
    }

    public async Task<Guid[]> GetPrivateVoiceVideoParticipantsAsync(Guid peerUserId)
    {
        EnsureConnected();
        return await _hub!.GetPrivateVoiceVideoParticipantsAsync(peerUserId);
    }

    public async Task SendPrivateWebRtcSignalAsync(Guid peerUserId, Guid targetUserId, string signalType, string payload)
    {
        EnsureConnected();
        await _hub!.SendPrivateWebRtcSignalAsync(peerUserId, targetUserId, signalType, payload);
    }

    public async Task SetScreenAnnotationStateAsync(Guid roomId, ScreenAnnotationState state)
    {
        EnsureConnected();
        await _hub!.SetScreenAnnotationStateAsync(roomId, state);
    }

    public async Task SendScreenAnnotationAsync(Guid roomId, ScreenAnnotationEvent annotationEvent)
    {
        EnsureConnected();
        await _hub!.SendScreenAnnotationAsync(roomId, annotationEvent);
    }

    public async Task<ScreenAnnotationState[]> GetScreenAnnotationSessionsAsync(Guid roomId)
    {
        EnsureConnected();
        return await _hub!.GetScreenAnnotationSessionsAsync(roomId);
    }

    public async Task SetPrivateScreenAnnotationStateAsync(Guid peerUserId, ScreenAnnotationState state)
    {
        EnsureConnected();
        await _hub!.SetPrivateScreenAnnotationStateAsync(peerUserId, state);
    }

    public async Task SendPrivateScreenAnnotationAsync(Guid peerUserId, ScreenAnnotationEvent annotationEvent)
    {
        EnsureConnected();
        await _hub!.SendPrivateScreenAnnotationAsync(peerUserId, annotationEvent);
    }

    public async Task<ScreenAnnotationState[]> GetPrivateScreenAnnotationSessionsAsync(Guid peerUserId)
    {
        EnsureConnected();
        return await _hub!.GetPrivateScreenAnnotationSessionsAsync(peerUserId);
    }

    void IChatHubReceiver.OnMessageReceived(ChatMessage message) => OnMessageReceived?.Invoke(message);
    void IChatHubReceiver.OnMessageDeleted(Guid roomId, Guid messageId) => OnMessageDeleted?.Invoke(roomId, messageId);
    void IChatHubReceiver.OnMessageEdited(Guid roomId, Guid messageId, string content, Guid[] mentionedUserIds, DateTime editedAt) => OnMessageEdited?.Invoke(roomId, messageId, content, mentionedUserIds, editedAt);
    void IChatHubReceiver.OnMessageReactionChanged(Guid roomId, Guid messageId, Guid userId, string emoji, bool added) => OnMessageReactionChanged?.Invoke(roomId, messageId, userId, emoji, added);
    void IChatHubReceiver.OnTyping(Guid roomId, Guid userId, string userName) => OnTyping?.Invoke(roomId, userId, userName);
    void IChatHubReceiver.OnRoomRead(Guid roomId, DateTime lastReadAt) => OnRoomRead?.Invoke(roomId, lastReadAt);
    void IChatHubReceiver.OnRoomDeleted(Guid roomId) => OnRoomDeleted?.Invoke(roomId);
    void IChatHubReceiver.OnRoomRenamed(Guid roomId, string newName) => OnRoomRenamed?.Invoke(roomId, newName);
    void IChatHubReceiver.OnUserJoinedRoom(Guid roomId, ChatMemberInfo member) => OnUserJoinedRoom?.Invoke(roomId, member);
    void IChatHubReceiver.OnUserLeftRoom(Guid roomId, Guid userId) => OnUserLeftRoom?.Invoke(roomId, userId);
    void IChatHubReceiver.OnUserKicked(Guid roomId, Guid userId, string reason) => OnUserKicked?.Invoke(roomId, userId, reason);
    void IChatHubReceiver.OnUserKickedFromVoiceVideo(Guid roomId, Guid userId, string reason) => OnUserKickedFromVoiceVideo?.Invoke(roomId, userId, reason);
    void IChatHubReceiver.OnUserMoved(Guid sourceRoomId, Guid targetRoomId, Guid userId) => OnUserMoved?.Invoke(sourceRoomId, targetRoomId, userId);
    void IChatHubReceiver.OnUserRoleChanged(Guid roomId, Guid userId, UserRole newRole) => OnUserRoleChanged?.Invoke(roomId, userId, newRole);
    void IChatHubReceiver.OnUserProfileUpdated(UserProfileUpdateInfo profile) => OnUserProfileUpdated?.Invoke(profile);
    void IChatHubReceiver.OnPrivateMessageReceived(PrivateMessage message) => OnPrivateMessageReceived?.Invoke(message);
    void IChatHubReceiver.OnPrivateMessagesRead(Guid userId) => OnPrivateMessagesRead?.Invoke(userId);
    void IChatHubReceiver.OnPrivateMessageDeleted(Guid senderId, Guid recipientId, Guid messageId) => OnPrivateMessageDeleted?.Invoke(senderId, recipientId, messageId);
    void IChatHubReceiver.OnPrivateMessageEdited(Guid senderId, Guid recipientId, Guid messageId, string content, DateTime editedAt) => OnPrivateMessageEdited?.Invoke(senderId, recipientId, messageId, content, editedAt);
    void IChatHubReceiver.OnPrivateMessageReactionChanged(Guid senderId, Guid recipientId, Guid messageId, Guid userId, string emoji, bool added) => OnPrivateMessageReactionChanged?.Invoke(senderId, recipientId, messageId, userId, emoji, added);
    void IChatHubReceiver.OnPrivateTyping(Guid userId, string userName) => OnPrivateTyping?.Invoke(userId, userName);
    void IChatHubReceiver.OnPrivateConversationRead(Guid readerUserId, Guid peerUserId) => OnPrivateConversationRead?.Invoke(readerUserId, peerUserId);
    void IChatHubReceiver.OnUserOnlineStatusChanged(Guid userId, bool isOnline) => OnUserOnlineStatusChanged?.Invoke(userId, isOnline);
    void IChatHubReceiver.OnWebRtcSignalReceived(Guid roomId, Guid fromUserId, string signalType, string payload) => OnWebRtcSignalReceived?.Invoke(roomId, fromUserId, signalType, payload);
    void IChatHubReceiver.OnUserJoinedVoiceVideo(Guid roomId, Guid userId) => OnUserJoinedVoiceVideo?.Invoke(roomId, userId);
    void IChatHubReceiver.OnUserLeftVoiceVideo(Guid roomId, Guid userId) => OnUserLeftVoiceVideo?.Invoke(roomId, userId);
    void IChatHubReceiver.OnPrivateCallIncoming(Guid callerUserId) => OnPrivateCallIncoming?.Invoke(callerUserId);
    void IChatHubReceiver.OnScreenAnnotationStateChanged(Guid contextId, ScreenAnnotationState state) => OnScreenAnnotationStateChanged?.Invoke(contextId, state);
    void IChatHubReceiver.OnScreenAnnotationReceived(Guid contextId, Guid fromUserId, ScreenAnnotationEvent annotationEvent) => OnScreenAnnotationReceived?.Invoke(contextId, fromUserId, annotationEvent);

    private void EnsureConnected()
    {
        if (_hub == null)
            throw new InvalidOperationException("Not connected to chat hub");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

}

using MagicOnion.Client;
using Shared.Hubs;
using Shared.Models;
using Shared.Requests;

namespace ChatAppBlazor.Services;

public class ChatClientService : IChatHubReceiver, IAsyncDisposable
{
    private readonly GrpcChannelProvider _channelProvider;
    private IChatHub? _hub;

    public event Action<ChatMessage>? OnMessageReceived;
    public event Action<Guid, Guid>? OnMessageDeleted;
    public event Action<Guid>? OnRoomDeleted;
    public event Action<Guid, ChatMemberInfo>? OnUserJoinedRoom;
    public event Action<Guid, Guid>? OnUserLeftRoom;
    public event Action<Guid, Guid, string>? OnUserKicked;
    public event Action<Guid, Guid, Guid>? OnUserMoved;
    public event Action<Guid, Guid, UserRole>? OnUserRoleChanged;
    public event Action<PrivateMessage>? OnPrivateMessageReceived;
    public event Action<Guid>? OnPrivateMessagesRead;
    public event Action<Guid, bool>? OnUserOnlineStatusChanged;
    public event Action<Guid, Guid, string, string>? OnWebRtcSignalReceived;
    public event Action<Guid, Guid>? OnUserJoinedVoiceVideo;
    public event Action<Guid, Guid>? OnUserLeftVoiceVideo;

    public bool IsConnected => _hub != null;

    public ChatClientService(GrpcChannelProvider channelProvider)
    {
        _channelProvider = channelProvider;
    }

    public async Task ConnectAsync()
    {
        if (_hub != null) return;

        var channel = _channelProvider.GetChannel();
        _hub = await StreamingHubClient.ConnectAsync<IChatHub, IChatHubReceiver>(
            channel, this);
    }

    public async Task DisconnectAsync()
    {
        if (_hub != null)
        {
            await _hub.DisposeAsync();
            _hub = null;
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

    public async Task SendMessageAsync(Guid roomId, string content, Guid[]? mentionedUserIds = null, AttachmentInfo[]? attachments = null)
    {
        EnsureConnected();
        await _hub!.SendMessageAsync(roomId, new SendMessageRequest
        {
            Content = content,
            MentionedUserIds = mentionedUserIds ?? [],
            Attachments = attachments ?? []
        });
    }

    public async Task DeleteMessageAsync(Guid roomId, Guid messageId)
    {
        EnsureConnected();
        await _hub!.DeleteMessageAsync(roomId, messageId);
    }

    public async Task KickUserAsync(Guid roomId, Guid userId)
    {
        EnsureConnected();
        await _hub!.KickUserAsync(roomId, userId);
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

    public async Task SendPrivateMessageAsync(Guid recipientId, string content, Guid[]? mentionedUserIds = null, AttachmentInfo[]? attachments = null)
    {
        EnsureConnected();
        await _hub!.SendPrivateMessageAsync(recipientId, new SendMessageRequest
        {
            Content = content,
            MentionedUserIds = mentionedUserIds ?? [],
            Attachments = attachments ?? []
        });
    }

    public async Task MarkPrivateMessageReadAsync(Guid senderId)
    {
        EnsureConnected();
        await _hub!.MarkPrivateMessageReadAsync(senderId);
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

    public async Task SendWebRtcSignalAsync(Guid roomId, Guid targetUserId, string signalType, string payload)
    {
        EnsureConnected();
        await _hub!.SendWebRtcSignalAsync(roomId, targetUserId, signalType, payload);
    }

    void IChatHubReceiver.OnMessageReceived(ChatMessage message) => OnMessageReceived?.Invoke(message);
    void IChatHubReceiver.OnMessageDeleted(Guid roomId, Guid messageId) => OnMessageDeleted?.Invoke(roomId, messageId);
    void IChatHubReceiver.OnRoomDeleted(Guid roomId) => OnRoomDeleted?.Invoke(roomId);
    void IChatHubReceiver.OnUserJoinedRoom(Guid roomId, ChatMemberInfo member) => OnUserJoinedRoom?.Invoke(roomId, member);
    void IChatHubReceiver.OnUserLeftRoom(Guid roomId, Guid userId) => OnUserLeftRoom?.Invoke(roomId, userId);
    void IChatHubReceiver.OnUserKicked(Guid roomId, Guid userId, string reason) => OnUserKicked?.Invoke(roomId, userId, reason);
    void IChatHubReceiver.OnUserMoved(Guid sourceRoomId, Guid targetRoomId, Guid userId) => OnUserMoved?.Invoke(sourceRoomId, targetRoomId, userId);
    void IChatHubReceiver.OnUserRoleChanged(Guid roomId, Guid userId, UserRole newRole) => OnUserRoleChanged?.Invoke(roomId, userId, newRole);
    void IChatHubReceiver.OnPrivateMessageReceived(PrivateMessage message) => OnPrivateMessageReceived?.Invoke(message);
    void IChatHubReceiver.OnPrivateMessagesRead(Guid userId) => OnPrivateMessagesRead?.Invoke(userId);
    void IChatHubReceiver.OnUserOnlineStatusChanged(Guid userId, bool isOnline) => OnUserOnlineStatusChanged?.Invoke(userId, isOnline);
    void IChatHubReceiver.OnWebRtcSignalReceived(Guid roomId, Guid fromUserId, string signalType, string payload) => OnWebRtcSignalReceived?.Invoke(roomId, fromUserId, signalType, payload);
    void IChatHubReceiver.OnUserJoinedVoiceVideo(Guid roomId, Guid userId) => OnUserJoinedVoiceVideo?.Invoke(roomId, userId);
    void IChatHubReceiver.OnUserLeftVoiceVideo(Guid roomId, Guid userId) => OnUserLeftVoiceVideo?.Invoke(roomId, userId);

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

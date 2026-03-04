using MagicOnion.Client;
using Shared.Hubs;
using Shared.Models;
using Shared.Requests;

namespace MusicBot.Services;

public sealed class ChatHubClient : IChatHubReceiver, IAsyncDisposable
{
    private readonly GrpcChannelProvider _channelProvider;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly object _roomsSync = new();
    private IChatHub? _hub;
    private readonly HashSet<Guid> _joinedRooms = [];
    private readonly HashSet<Guid> _targetRooms = [];
    private CancellationTokenSource? _reconnectCts;
    private bool _intentionalDisconnect;

    public bool IsConnected => _hub != null;

    public event Action<ChatMessage>? MessageReceived;
    public event Action<Guid, Guid>? UserJoinedVoiceVideo;
    public event Action<Guid, Guid>? UserLeftVoiceVideo;
    public event Action<bool>? ConnectionStateChanged;

    public ChatHubClient(GrpcChannelProvider channelProvider)
    {
        _channelProvider = channelProvider;
    }

    public async Task ConnectAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            if (_hub != null) return;

            _intentionalDisconnect = false;
            _reconnectCts?.Cancel();
            _reconnectCts = new CancellationTokenSource();

            _hub = await StreamingHubClient.ConnectAsync<IChatHub, IChatHubReceiver>(
                _channelProvider.GetChannel(),
                this);

            _ = WatchForDisconnectAsync(_hub, _reconnectCts.Token);
            await RejoinTargetRoomsAsync(_reconnectCts.Token);
            ConnectionStateChanged?.Invoke(true);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            _intentionalDisconnect = true;
            _reconnectCts?.Cancel();
            _reconnectCts = null;

            lock (_roomsSync)
            {
                _joinedRooms.Clear();
                _targetRooms.Clear();
            }

            if (_hub != null)
            {
                await _hub.DisposeAsync();
                _hub = null;
            }

            ConnectionStateChanged?.Invoke(false);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task JoinRoomAsync(Guid roomId)
    {
        lock (_roomsSync)
        {
            _targetRooms.Add(roomId);
        }

        EnsureConnected();
        lock (_roomsSync)
        {
            if (_joinedRooms.Contains(roomId))
            {
                return;
            }
        }

        await _hub!.JoinRoomAsync(roomId);
        lock (_roomsSync)
        {
            _joinedRooms.Add(roomId);
        }
    }

    public async Task SendMessageAsync(Guid roomId, string content)
    {
        EnsureConnected();
        await _hub!.SendMessageAsync(roomId, new SendMessageRequest
        {
            Content = content
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

    private async Task WatchForDisconnectAsync(IChatHub watchedHub, CancellationToken ct)
    {
        try
        {
            await watchedHub.WaitForDisconnect();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // Unexpected watcher failure is treated as a disconnect.
        }

        if (_intentionalDisconnect || ct.IsCancellationRequested)
        {
            return;
        }

        if (!ReferenceEquals(_hub, watchedHub))
        {
            return;
        }

        _hub = null;
        lock (_roomsSync)
        {
            _joinedRooms.Clear();
        }
        ConnectionStateChanged?.Invoke(false);

        await ReconnectLoopAsync(ct);
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        var delayMs = 1000;
        const int maxDelayMs = 30_000;

        while (!ct.IsCancellationRequested && !_intentionalDisconnect)
        {
            try
            {
                await Task.Delay(delayMs, ct);
                _channelProvider.ResetChannel();

                var hub = await StreamingHubClient.ConnectAsync<IChatHub, IChatHubReceiver>(
                    _channelProvider.GetChannel(),
                    this);

                _hub = hub;
                await RejoinTargetRoomsAsync(ct);
                ConnectionStateChanged?.Invoke(true);
                _ = WatchForDisconnectAsync(hub, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                delayMs = Math.Min(delayMs * 2, maxDelayMs);
            }
        }
    }

    private async Task RejoinTargetRoomsAsync(CancellationToken ct)
    {
        Guid[] rooms;
        lock (_roomsSync)
        {
            _joinedRooms.Clear();
            rooms = [.. _targetRooms];
        }

        foreach (var roomId in rooms)
        {
            ct.ThrowIfCancellationRequested();
            if (_hub == null)
            {
                return;
            }

            try
            {
                await _hub.JoinRoomAsync(roomId);
                lock (_roomsSync)
                {
                    _joinedRooms.Add(roomId);
                }
            }
            catch
            {
                // Ignore individual room join failures; next reconnect/explicit join will retry.
            }
        }
    }

    private void EnsureConnected()
    {
        if (_hub == null)
        {
            throw new InvalidOperationException("Chat hub is not connected.");
        }
    }

    void IChatHubReceiver.OnMessageReceived(ChatMessage message) => MessageReceived?.Invoke(message);
    void IChatHubReceiver.OnUserJoinedVoiceVideo(Guid roomId, Guid userId) => UserJoinedVoiceVideo?.Invoke(roomId, userId);
    void IChatHubReceiver.OnUserLeftVoiceVideo(Guid roomId, Guid userId) => UserLeftVoiceVideo?.Invoke(roomId, userId);

    void IChatHubReceiver.OnMessageDeleted(Guid roomId, Guid messageId) { }
    void IChatHubReceiver.OnRoomDeleted(Guid roomId) { }
    void IChatHubReceiver.OnUserJoinedRoom(Guid roomId, ChatMemberInfo member) { }
    void IChatHubReceiver.OnUserLeftRoom(Guid roomId, Guid userId) { }
    void IChatHubReceiver.OnUserKicked(Guid roomId, Guid userId, string reason) { }
    void IChatHubReceiver.OnUserKickedFromVoiceVideo(Guid roomId, Guid userId, string reason) { }
    void IChatHubReceiver.OnUserMoved(Guid sourceRoomId, Guid targetRoomId, Guid userId) { }
    void IChatHubReceiver.OnUserRoleChanged(Guid roomId, Guid userId, UserRole newRole) { }
    void IChatHubReceiver.OnPrivateMessageReceived(PrivateMessage message) { }
    void IChatHubReceiver.OnPrivateMessagesRead(Guid userId) { }
    void IChatHubReceiver.OnUserOnlineStatusChanged(Guid userId, bool isOnline) { }
    void IChatHubReceiver.OnWebRtcSignalReceived(Guid roomId, Guid fromUserId, string signalType, string payload) { }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _connectionGate.Dispose();
    }
}

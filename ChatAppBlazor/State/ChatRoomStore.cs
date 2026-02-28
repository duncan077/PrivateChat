using ChatAppBlazor.Services;
using Shared.Models;

namespace ChatAppBlazor.State;

/// <summary>
/// Manages chat room state: room list, selected room, messages, and members.
/// Components subscribe to OnChange to re-render when state updates.
/// </summary>
public class ChatRoomStore : IDisposable
{
    private readonly ApiClientService _api;
    private readonly ChatClientService _chat;
    private readonly AuthStateProvider _auth;

    public ChatRoomInfo[] Rooms { get; private set; } = [];
    public Guid? SelectedRoomId { get; private set; }
    public ChatRoomInfo? SelectedRoom { get; private set; }
    public List<ChatMessage> Messages { get; private set; } = [];
    public ChatMemberInfo[] Members { get; private set; } = [];
    public UserRole CurrentUserRole { get; private set; } = UserRole.Member;
    public string? Error { get; private set; }

    public event Action? OnChange;

    public ChatRoomStore(ApiClientService api, ChatClientService chat, AuthStateProvider auth)
    {
        _api = api;
        _chat = chat;
        _auth = auth;

        _chat.OnMessageReceived += HandleMessageReceived;
        _chat.OnMessageDeleted += HandleMessageDeleted;
        _chat.OnUserJoinedRoom += HandleUserJoined;
        _chat.OnUserLeftRoom += HandleUserLeft;
        _chat.OnRoomDeleted += HandleRoomDeleted;
        _chat.OnUserKicked += HandleUserKicked;
        _chat.OnUserRoleChanged += HandleUserRoleChanged;
        _chat.OnConnectionStateChanged += HandleConnectionStateChanged;
    }

    public async Task LoadRoomsAsync()
    {
        try
        {
            Error = null;
            Rooms = await _api.GetRoomsAsync();
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Error = $"Failed to load rooms: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task SelectRoomAsync(Guid roomId)
    {
        if (SelectedRoomId == roomId) return;

        // Leave previous room
        if (SelectedRoomId != null)
        {
            try { await _chat.LeaveRoomAsync(SelectedRoomId.Value); } catch { }
        }

        SelectedRoomId = roomId;
        SelectedRoom = Rooms.FirstOrDefault(r => r.Id == roomId);
        Messages.Clear();
        Error = null;
        NotifyChanged();

        try
        {
            await _chat.JoinRoomAsync(roomId);
            var msgs = await _api.GetRoomMessagesAsync(roomId);
            foreach (var message in msgs)
            {
                _api.NormalizeAttachments(message.Attachments);
            }
            Messages = msgs.Reverse().ToList();
            Members = await _api.GetRoomMembersAsync(roomId);
            CurrentUserRole = Members.FirstOrDefault(m => m.UserId == _auth.CurrentUser?.Id)?.Role ?? UserRole.Member;
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Error = $"Failed to join room: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task SendMessageAsync(string content, AttachmentInfo[]? attachments = null)
    {
        if (SelectedRoomId == null) return;
        var hasText = !string.IsNullOrWhiteSpace(content);
        var hasAttachments = attachments is { Length: > 0 };
        if (!hasText && !hasAttachments) return;

        try
        {
            await _chat.SendMessageAsync(SelectedRoomId.Value, content.Trim(), attachments: attachments);
        }
        catch (Exception ex)
        {
            Error = $"Failed to send message: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task DeleteMessageAsync(Guid messageId)
    {
        if (SelectedRoomId == null) return;
        try
        {
            await _chat.DeleteMessageAsync(SelectedRoomId.Value, messageId);
        }
        catch (Exception ex)
        {
            Error = $"Failed to delete message: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task CreateRoomAsync(string name, string? description, bool voiceEnabled, bool videoEnabled)
    {
        try
        {
            await _api.CreateRoomAsync(name, description, voiceEnabled, videoEnabled);
            await LoadRoomsAsync();
        }
        catch (Exception ex)
        {
            Error = $"Failed to create room: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task DeleteRoomAsync(Guid roomId)
    {
        try
        {
            await _chat.DeleteRoomAsync(roomId);
            await LoadRoomsAsync();
            HandleRoomDeleted(roomId);
        }
        catch (Exception ex)
        {
            Error = $"Failed to delete room: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task KickUserAsync(Guid userId)
    {
        if (SelectedRoomId == null) return;
        try
        {
            await _chat.KickUserAsync(SelectedRoomId.Value, userId);
            Members = await _api.GetRoomMembersAsync(SelectedRoomId.Value);
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Error = $"Failed to kick user: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task SetUserRoleAsync(Guid userId, UserRole role)
    {
        if (SelectedRoomId == null) return;
        try
        {
            await _chat.SetUserRoleAsync(SelectedRoomId.Value, userId, role);
            Members = await _api.GetRoomMembersAsync(SelectedRoomId.Value);
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Error = $"Failed to change role: {ex.Message}";
            NotifyChanged();
        }
    }

    private void HandleMessageReceived(ChatMessage message)
    {
        _api.NormalizeAttachments(message.Attachments);
        if (message.RoomId == SelectedRoomId)
        {
            Messages.Add(message);
            NotifyChanged();
        }
    }

    private void HandleMessageDeleted(Guid roomId, Guid messageId)
    {
        if (roomId == SelectedRoomId)
        {
            var msg = Messages.FirstOrDefault(m => m.Id == messageId);
            if (msg != null) msg.IsDeleted = true;
            NotifyChanged();
        }
    }

    private async void HandleUserJoined(Guid roomId, ChatMemberInfo member)
    {
        if (roomId != SelectedRoomId) return;
        try
        {
            Members = await _api.GetRoomMembersAsync(roomId);
            NotifyChanged();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatRoomStore] HandleUserJoined error: {ex.Message}");
        }
    }

    private async void HandleUserLeft(Guid roomId, Guid userId)
    {
        if (roomId != SelectedRoomId) return;
        try
        {
            Members = await _api.GetRoomMembersAsync(roomId);
            NotifyChanged();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatRoomStore] HandleUserLeft error: {ex.Message}");
        }
    }

    private void HandleRoomDeleted(Guid roomId)
    {
        Rooms = Rooms.Where(r => r.Id != roomId).ToArray();
        if (SelectedRoomId == roomId)
        {
            SelectedRoomId = null;
            SelectedRoom = null;
            Messages.Clear();
            Members = [];
        }
        NotifyChanged();
    }

    private void HandleUserKicked(Guid roomId, Guid userId, string reason)
    {
        if (roomId == SelectedRoomId && userId == _auth.CurrentUser?.Id)
        {
            SelectedRoomId = null;
            SelectedRoom = null;
            Messages.Clear();
            Members = [];
            Error = $"You were kicked: {reason}";
        }
        NotifyChanged();
    }

    private void HandleUserRoleChanged(Guid roomId, Guid userId, UserRole newRole)
    {
        if (roomId == SelectedRoomId && userId == _auth.CurrentUser?.Id)
        {
            CurrentUserRole = newRole;
            NotifyChanged();
        }
    }

    /// <summary>
    /// When the gRPC connection drops and reconnects, the server-side hub has a
    /// fresh _joinedRooms set (empty). We must re-join the currently selected room
    /// so that SendMessageAsync works and messages are received again.
    /// </summary>
    private async void HandleConnectionStateChanged(bool connected)
    {
        if (!connected || SelectedRoomId == null) return;

        var roomId = SelectedRoomId.Value;
        try
        {
            System.Diagnostics.Debug.WriteLine($"[ChatRoomStore] Reconnected — re-joining room {roomId}");
            await _chat.JoinRoomAsync(roomId);

            // Reload messages that may have arrived while disconnected
            var msgs = await _api.GetRoomMessagesAsync(roomId);
            foreach (var message in msgs)
            {
                _api.NormalizeAttachments(message.Attachments);
            }
            Messages = msgs.Reverse().ToList();
            NotifyChanged();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatRoomStore] Re-join after reconnect failed: {ex.Message}");
        }
    }

    private void NotifyChanged() => OnChange?.Invoke();

    public void Dispose()
    {
        _chat.OnMessageReceived -= HandleMessageReceived;
        _chat.OnMessageDeleted -= HandleMessageDeleted;
        _chat.OnUserJoinedRoom -= HandleUserJoined;
        _chat.OnUserLeftRoom -= HandleUserLeft;
        _chat.OnRoomDeleted -= HandleRoomDeleted;
        _chat.OnUserKicked -= HandleUserKicked;
        _chat.OnUserRoleChanged -= HandleUserRoleChanged;
        _chat.OnConnectionStateChanged -= HandleConnectionStateChanged;
    }
}

using PrivateChatApp.Services;
using Shared.Models;

namespace PrivateChatApp.State;

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

    public async Task SendMessageAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || SelectedRoomId == null) return;
        try
        {
            await _chat.SendMessageAsync(SelectedRoomId.Value, content);
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
    }
}

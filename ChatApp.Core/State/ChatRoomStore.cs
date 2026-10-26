using ChatApp.Core.Services;
using ChatApp.Core.Abstractions;
using Shared.Models;
using System.Text.Json;

namespace ChatApp.Core.State;

/// <summary>
/// Manages chat room state: room list, selected room, messages, and members.
/// Components subscribe to OnChange to re-render when state updates.
/// </summary>
public enum RoomNotificationMode
{
    All = 0,
    MentionsOnly = 1,
    Muted = 2
}

public class ChatRoomStore : IDisposable
{
    private const string RoomNotificationModesPrefPrefix = "notifications.room.modes.";
    private const string LegacyMutedRoomNotificationsPrefPrefix = "notifications.muted.rooms.";
    private static readonly TimeSpan ServerMembersRefreshInterval = TimeSpan.FromSeconds(20);
    private readonly ApiClientService _api;
    private readonly ChatClientService _chat;
    private readonly AuthStateProvider _auth;
    private readonly IAppPreferences _preferences;
    private readonly CancellationTokenSource _serverMembersRefreshCts = new();
    private readonly Task _serverMembersRefreshLoopTask;
    private int _serverMembersRefreshInProgress;
    private Dictionary<Guid, RoomNotificationMode> _roomNotificationModes = [];
    private Guid? _notificationPrefsUserId;

    public ChatRoomInfo[] Rooms { get; private set; } = [];
    public Guid? SelectedRoomId { get; private set; }
    public ChatRoomInfo? SelectedRoom { get; private set; }
    public List<ChatMessage> Messages { get; private set; } = [];
    public ChatMemberInfo[] Members { get; private set; } = [];
    public ServerMemberInfo[] ServerMembers { get; private set; } = [];
    public UserRole CurrentUserRole { get; private set; } = UserRole.Member;
    public string? Error { get; private set; }

    public event Action? OnChange;
    /// <summary>Fired when the current user is moved to a new room by a moderator.</summary>
    public event Action<Guid>? OnCurrentUserMoved;

    public ChatRoomStore(
        ApiClientService api,
        ChatClientService chat,
        AuthStateProvider auth,
        IAppPreferences preferences)
    {
        _api = api;
        _chat = chat;
        _auth = auth;
        _preferences = preferences;

        _chat.OnMessageReceived += HandleMessageReceived;
        _chat.OnMessageDeleted += HandleMessageDeleted;
        _chat.OnUserJoinedRoom += HandleUserJoined;
        _chat.OnUserLeftRoom += HandleUserLeft;
        _chat.OnRoomDeleted += HandleRoomDeleted;
        _chat.OnRoomRenamed += HandleRoomRenamed;
        _chat.OnUserKicked += HandleUserKicked;
        _chat.OnUserMoved += HandleUserMoved;
        _chat.OnUserRoleChanged += HandleUserRoleChanged;
        _chat.OnUserProfileUpdated += HandleUserProfileUpdated;
        _chat.OnUserOnlineStatusChanged += HandleUserOnlineStatusChanged;
        _chat.OnConnectionStateChanged += HandleConnectionStateChanged;

        _serverMembersRefreshLoopTask = RunServerMembersRefreshLoopAsync(_serverMembersRefreshCts.Token);
    }

    public bool IsRoomNotificationsMuted(Guid roomId)
    {
        return GetRoomNotificationMode(roomId) == RoomNotificationMode.Muted;
    }

    public bool IsRoomNotificationsMentionsOnly(Guid roomId)
        => GetRoomNotificationMode(roomId) == RoomNotificationMode.MentionsOnly;

    public RoomNotificationMode GetRoomNotificationMode(Guid roomId)
    {
        if (roomId == Guid.Empty) return RoomNotificationMode.All;
        EnsureNotificationMuteStateLoaded();
        return _roomNotificationModes.TryGetValue(roomId, out var mode)
            ? mode
            : RoomNotificationMode.All;
    }

    public RoomNotificationMode? SelectedRoomNotificationMode
        => SelectedRoomId != null ? GetRoomNotificationMode(SelectedRoomId.Value) : null;

    public bool IsSelectedRoomNotificationsMuted
        => SelectedRoomId != null && IsRoomNotificationsMuted(SelectedRoomId.Value);

    public void SetRoomNotificationsMuted(Guid roomId, bool muted)
    {
        SetRoomNotificationMode(roomId, muted ? RoomNotificationMode.Muted : RoomNotificationMode.All);
    }

    public void ToggleRoomNotificationsMuted(Guid roomId)
    {
        if (roomId == Guid.Empty) return;
        SetRoomNotificationsMuted(roomId, !IsRoomNotificationsMuted(roomId));
    }

    public void SetRoomNotificationMode(Guid roomId, RoomNotificationMode mode)
    {
        if (roomId == Guid.Empty) return;
        EnsureNotificationMuteStateLoaded();

        var currentMode = GetRoomNotificationMode(roomId);
        if (currentMode == mode) return;

        if (mode == RoomNotificationMode.All)
        {
            _roomNotificationModes.Remove(roomId);
        }
        else
        {
            _roomNotificationModes[roomId] = mode;
        }

        SaveRoomNotificationModes();
        NotifyChanged();
    }

    public void CycleRoomNotificationMode(Guid roomId)
    {
        if (roomId == Guid.Empty) return;

        var nextMode = GetRoomNotificationMode(roomId) switch
        {
            RoomNotificationMode.All => RoomNotificationMode.MentionsOnly,
            RoomNotificationMode.MentionsOnly => RoomNotificationMode.Muted,
            _ => RoomNotificationMode.All
        };

        SetRoomNotificationMode(roomId, nextMode);
    }

    public async Task LoadRoomsAsync()
    {
        try
        {
            Error = null;
            await RefreshRoomsSnapshotAsync();
            if (ServerMembers.Length == 0)
            {
                await RefreshServerMembersAsync();
            }
            else
            {
                CurrentUserRole = GetCurrentUserGlobalRole();
            }
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Error = $"Failed to load rooms: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task LoadServerMembersAsync() => await RefreshServerMembersAsync();

    public ChatMemberInfo? GetSelectedRoomMember(Guid userId)
        => Members.FirstOrDefault(m => m.UserId == userId);

    public bool IsUserInSelectedRoom(Guid userId)
        => GetSelectedRoomMember(userId) != null;

    public async Task SelectRoomAsync(Guid roomId, bool keepPreviousRoomSubscription = false)
    {
        if (SelectedRoomId == roomId) return;

        // Leave previous room
        if (SelectedRoomId != null && !keepPreviousRoomSubscription)
        {
            try { await _chat.LeaveRoomAsync(SelectedRoomId.Value); } catch { }
        }

        SelectedRoomId = roomId;
        SelectedRoom = Rooms.FirstOrDefault(r => r.Id == roomId);
        Messages.Clear();
        Members = [];
        CurrentUserRole = GetCurrentUserGlobalRole();
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
            await RefreshSelectedRoomMembersAsync(roomId);
            await RefreshServerMembersAsync();
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Error = $"Failed to join room: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task SendMessageAsync(string content, AttachmentInfo[]? attachments = null, Guid[]? mentionedUserIds = null)
    {
        if (SelectedRoomId == null) return;
        var hasText = !string.IsNullOrWhiteSpace(content);
        var hasAttachments = attachments is { Length: > 0 };
        if (!hasText && !hasAttachments) return;

        try
        {
            await _chat.SendMessageAsync(
                SelectedRoomId.Value,
                content.Trim(),
                mentionedUserIds: mentionedUserIds,
                attachments: attachments);
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
            await RefreshServerMembersAsync();
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
            await RefreshServerMembersAsync();
            HandleRoomDeleted(roomId);
        }
        catch (Exception ex)
        {
            Error = $"Failed to delete room: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task RenameRoomAsync(Guid roomId, string name)
    {
        Error = null;
        try
        {
            await _chat.RenameRoomAsync(roomId, name);
            // The hub broadcasts OnRoomRenamed to all clients (including us),
            // which HandleRoomRenamed will pick up and update state.
        }
        catch (Exception ex)
        {
            Error = $"Failed to rename room: {ex.Message}";
            NotifyChanged();
        }
    }

    private void HandleRoomRenamed(Guid roomId, string newName)
    {
        Rooms = Rooms.Select(r =>
        {
            if (r.Id != roomId) return r;
            // Copy all fields but update the name
            r.Name = newName;
            return r;
        }).ToArray();

        if (SelectedRoomId == roomId && SelectedRoom != null)
            SelectedRoom.Name = newName;

        NotifyChanged();
    }

    public async Task KickUserAsync(Guid userId)
    {
        if (SelectedRoomId == null) return;
        try
        {
            await _chat.KickUserAsync(SelectedRoomId.Value, userId);
            await RefreshSelectedRoomMembersAsync(SelectedRoomId.Value);
            await RefreshServerMembersAsync();
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Error = $"Failed to kick user: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task KickUserFromCurrentCallAsync(Guid userId, string reason = "Kicked from call by moderator")
    {
        if (SelectedRoomId == null) return;
        try
        {
            await _chat.KickUserFromVoiceVideoAsync(SelectedRoomId.Value, userId, reason);
        }
        catch (Exception ex)
        {
            Error = $"Failed to kick user from call: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task MoveUserAsync(Guid userId, Guid targetRoomId)
    {
        if (SelectedRoomId == null) return;
        if (targetRoomId == SelectedRoomId.Value) return;

        try
        {
            await _chat.MoveUserAsync(SelectedRoomId.Value, targetRoomId, userId);
            await LoadRoomsAsync();
            await RefreshSelectedRoomMembersAsync(SelectedRoomId.Value);
            await RefreshServerMembersAsync();
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Error = $"Failed to move user: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task SetUserRoleAsync(Guid userId, UserRole role)
    {
        try
        {
            await _chat.SetUserRoleAsync(SelectedRoomId ?? Guid.Empty, userId, role);
            await RefreshServerMembersAsync();
            if (SelectedRoomId != null)
            {
                await RefreshSelectedRoomMembersAsync(SelectedRoomId.Value);
            }
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

    private async void HandleUserJoined(Guid roomId, ChatMemberInfo _)
    {
        if (roomId != SelectedRoomId) return;

        try
        {
            await RefreshRoomsSnapshotAsync();
            await RefreshSelectedRoomMembersAsync(roomId);
            await RefreshServerMembersAsync();
            NotifyChanged();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatRoomStore] HandleUserJoined error: {ex.Message}");
        }
    }

    private async void HandleUserLeft(Guid roomId, Guid _)
    {
        if (roomId != SelectedRoomId) return;

        try
        {
            await RefreshRoomsSnapshotAsync();
            await RefreshSelectedRoomMembersAsync(roomId);
            await RefreshServerMembersAsync();
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
            CurrentUserRole = UserRole.Member;
        }

        NotifyChanged();
    }

    private async void HandleUserKicked(Guid roomId, Guid userId, string reason)
    {
        if (roomId == SelectedRoomId && userId == _auth.CurrentUser?.Id)
        {
            SelectedRoomId = null;
            SelectedRoom = null;
            Messages.Clear();
            Members = [];
            CurrentUserRole = UserRole.Member;
            Error = $"You were kicked: {reason}";
        }

        try
        {
            await RefreshRoomsSnapshotAsync();
            await RefreshServerMembersAsync();
        }
        catch { }

        NotifyChanged();
    }

    private async void HandleUserMoved(Guid sourceRoomId, Guid targetRoomId, Guid userId)
    {
        var affectedSelectedRoom = SelectedRoomId == sourceRoomId || SelectedRoomId == targetRoomId;
        if (!affectedSelectedRoom && userId != _auth.CurrentUser?.Id)
        {
            try
            {
                await RefreshRoomsSnapshotAsync();
                await RefreshServerMembersAsync();
            }
            catch { }
            NotifyChanged();
            return;
        }

        try
        {
            await RefreshRoomsSnapshotAsync();

            if (SelectedRoomId == sourceRoomId || SelectedRoomId == targetRoomId)
            {
                await RefreshSelectedRoomMembersAsync(SelectedRoomId!.Value);
            }

            await RefreshServerMembersAsync();

            if (userId == _auth.CurrentUser?.Id && SelectedRoomId == sourceRoomId)
            {
                await SelectRoomAsync(targetRoomId);
                OnCurrentUserMoved?.Invoke(targetRoomId);
            }
            else
            {
                NotifyChanged();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatRoomStore] HandleUserMoved error: {ex.Message}");
        }
    }

    private async void HandleUserRoleChanged(Guid roomId, Guid userId, UserRole newRole)
    {
        try
        {
            await RefreshServerMembersAsync();
            if (SelectedRoomId != null)
            {
                await RefreshSelectedRoomMembersAsync(SelectedRoomId.Value);
            }
            NotifyChanged();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatRoomStore] HandleUserRoleChanged error: {ex.Message}");
        }
    }

    private void HandleUserOnlineStatusChanged(Guid userId, bool isOnline)
    {
        var changed = false;

        var member = ServerMembers.FirstOrDefault(m => m.UserId == userId);
        if (member != null && member.IsOnline != isOnline)
        {
            member.IsOnline = isOnline;
            changed = true;
        }

        // The room member list carries its own IsOnline; updating only the server list
        // left it stale until the next full refresh, so the "in this room" section
        // showed people offline who were demonstrably typing.
        var roomMember = Members.FirstOrDefault(m => m.UserId == userId);
        if (roomMember != null && roomMember.IsOnline != isOnline)
        {
            roomMember.IsOnline = isOnline;
            changed = true;
        }

        if (changed) NotifyChanged();
    }

    private void HandleUserProfileUpdated(UserProfileUpdateInfo profile)
    {
        var changed = false;

        var serverMember = ServerMembers.FirstOrDefault(m => m.UserId == profile.UserId);
        if (serverMember != null)
        {
            serverMember.DisplayName = profile.DisplayName;
            serverMember.AvatarUrl = profile.AvatarUrl;
            changed = true;
        }

        var roomMember = Members.FirstOrDefault(m => m.UserId == profile.UserId);
        if (roomMember != null)
        {
            roomMember.DisplayName = profile.DisplayName;
            roomMember.AvatarUrl = profile.AvatarUrl;
            changed = true;
        }

        if (_auth.CurrentUser?.Id == profile.UserId)
        {
            _auth.UpdateCurrentUserProfile(profile.DisplayName, profile.AvatarUrl);
            changed = true;
        }

        if (changed)
        {
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
            System.Diagnostics.Debug.WriteLine($"[ChatRoomStore] Reconnected - re-joining room {roomId}");
            await _chat.JoinRoomAsync(roomId);

            // Reload messages that may have arrived while disconnected
            var msgs = await _api.GetRoomMessagesAsync(roomId);
            foreach (var message in msgs)
            {
                _api.NormalizeAttachments(message.Attachments);
            }

            Messages = msgs.Reverse().ToList();
            await RefreshRoomsSnapshotAsync();
            await RefreshSelectedRoomMembersAsync(roomId);
            await RefreshServerMembersAsync();
            NotifyChanged();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatRoomStore] Re-join after reconnect failed: {ex.Message}");
        }
    }

    private async Task RefreshSelectedRoomMembersAsync(Guid roomId)
    {
        var members = await _api.GetRoomMembersAsync(roomId);
        OverlayPresence(members);
        Members = members;
    }

    /// <summary>
    /// Copies live presence onto a freshly fetched room member list.
    /// <para>
    /// The server's <c>GetRoomMembersAsync</c> projection never sets <c>IsOnline</c>, so
    /// every fetch arrives all-offline and would erase what presence events and the
    /// server-member list already know. The server list is the presence authority here;
    /// the server itself stays unchanged by design.
    /// </para>
    /// </summary>
    private void OverlayPresence(ChatMemberInfo[] members)
    {
        foreach (var member in members)
        {
            var server = ServerMembers.FirstOrDefault(s => s.UserId == member.UserId);
            if (server != null)
                member.IsOnline = server.IsOnline;
        }
    }

    private async Task RefreshRoomsSnapshotAsync()
    {
        Rooms = await _api.GetRoomsAsync();
        SelectedRoom = SelectedRoomId == null
            ? null
            : Rooms.FirstOrDefault(r => r.Id == SelectedRoomId.Value);
    }

    private async Task RefreshServerMembersAsync()
    {
        await RefreshServerMembersCoreAsync(notifyWhenChanged: false);
    }

    private async Task<bool> RefreshServerMembersCoreAsync(bool notifyWhenChanged)
    {
        if (Interlocked.Exchange(ref _serverMembersRefreshInProgress, 1) != 0)
        {
            return false;
        }

        try
        {
            var nextMembers = await _api.GetServerMembersAsync();
            var changed = !AreServerMembersEquivalent(ServerMembers, nextMembers);
            ServerMembers = nextMembers;
            CurrentUserRole = GetCurrentUserGlobalRole();

            // On the first room selection this refresh lands after the room member
            // fetch, whose overlay found an empty server list and left everyone
            // offline — so the overlay is repeated with real data.
            OverlayPresence(Members);

            if (notifyWhenChanged && changed)
            {
                NotifyChanged();
            }

            return changed;
        }
        finally
        {
            Interlocked.Exchange(ref _serverMembersRefreshInProgress, 0);
        }
    }

    private async Task RunServerMembersRefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ServerMembersRefreshInterval, cancellationToken);
                if (cancellationToken.IsCancellationRequested || !_auth.IsAuthenticated)
                {
                    continue;
                }

                await RefreshServerMembersCoreAsync(notifyWhenChanged: true);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatRoomStore] Background member refresh failed: {ex.Message}");
            }
        }
    }

    private static bool AreServerMembersEquivalent(ServerMemberInfo[] currentMembers, ServerMemberInfo[] nextMembers)
    {
        if (ReferenceEquals(currentMembers, nextMembers))
        {
            return true;
        }

        currentMembers ??= [];
        nextMembers ??= [];
        if (currentMembers.Length != nextMembers.Length)
        {
            return false;
        }

        var currentOrdered = currentMembers
            .OrderBy(member => member.UserId)
            .ToArray();
        var nextOrdered = nextMembers
            .OrderBy(member => member.UserId)
            .ToArray();

        for (var i = 0; i < currentOrdered.Length; i++)
        {
            var current = currentOrdered[i];
            var next = nextOrdered[i];

            if (current.UserId != next.UserId
                || current.IsOnline != next.IsOnline
                || current.Role != next.Role
                || !string.Equals(current.UserName, next.UserName, StringComparison.Ordinal)
                || !string.Equals(current.DisplayName, next.DisplayName, StringComparison.Ordinal)
                || !string.Equals(current.AvatarUrl, next.AvatarUrl, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private UserRole GetCurrentUserGlobalRole()
        => ServerMembers.FirstOrDefault(m => m.UserId == _auth.CurrentUser?.Id)?.Role ?? UserRole.Member;

    private void NotifyChanged() => OnChange?.Invoke();

    private void EnsureNotificationMuteStateLoaded()
    {
        var userId = _auth.CurrentUser?.Id;
        if (_notificationPrefsUserId == userId)
        {
            return;
        }

        _notificationPrefsUserId = userId;
        _roomNotificationModes = LoadRoomNotificationModes();
    }

    private string GetRoomNotificationModesPrefKey()
    {
        var userToken = _auth.CurrentUser?.Id.ToString("D") ?? "anonymous";
        return $"{RoomNotificationModesPrefPrefix}{userToken}";
    }

    private string GetLegacyMutedRoomNotificationsPrefKey()
    {
        var userToken = _auth.CurrentUser?.Id.ToString("D") ?? "anonymous";
        return $"{LegacyMutedRoomNotificationsPrefPrefix}{userToken}";
    }

    private Dictionary<Guid, RoomNotificationMode> LoadRoomNotificationModes()
    {
        try
        {
            var raw = _preferences.Get(GetRoomNotificationModesPrefKey(), string.Empty);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var serializedModes = JsonSerializer.Deserialize<Dictionary<string, int>>(raw) ?? [];
                var result = new Dictionary<Guid, RoomNotificationMode>();

                foreach (var pair in serializedModes)
                {
                    if (!Guid.TryParse(pair.Key, out var roomId) || roomId == Guid.Empty)
                    {
                        continue;
                    }

                    if (!Enum.IsDefined(typeof(RoomNotificationMode), pair.Value))
                    {
                        continue;
                    }

                    var mode = (RoomNotificationMode)pair.Value;
                    if (mode != RoomNotificationMode.All)
                    {
                        result[roomId] = mode;
                    }
                }

                return result;
            }

            // Migration from previous bool mute-only storage.
            var legacyRaw = _preferences.Get(GetLegacyMutedRoomNotificationsPrefKey(), string.Empty);
            if (string.IsNullOrWhiteSpace(legacyRaw))
            {
                return [];
            }

            var mutedRoomIds = JsonSerializer.Deserialize<Guid[]>(legacyRaw) ?? [];
            return mutedRoomIds
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToDictionary(id => id, _ => RoomNotificationMode.Muted);
        }
        catch
        {
            return [];
        }
    }

    private void SaveRoomNotificationModes()
    {
        try
        {
            var payload = _roomNotificationModes
                .Where(pair => pair.Key != Guid.Empty && pair.Value != RoomNotificationMode.All)
                .ToDictionary(pair => pair.Key.ToString("D"), pair => (int)pair.Value);

            _preferences.Set(GetRoomNotificationModesPrefKey(), JsonSerializer.Serialize(payload));
        }
        catch
        {
            // Ignore preference persistence failures.
        }
    }

    public void Dispose()
    {
        _serverMembersRefreshCts.Cancel();
        try
        {
            _serverMembersRefreshLoopTask.Wait(250);
        }
        catch
        {
            // Ignore disposal races.
        }
        _serverMembersRefreshCts.Dispose();

        _chat.OnMessageReceived -= HandleMessageReceived;
        _chat.OnMessageDeleted -= HandleMessageDeleted;
        _chat.OnUserJoinedRoom -= HandleUserJoined;
        _chat.OnUserLeftRoom -= HandleUserLeft;
        _chat.OnRoomDeleted -= HandleRoomDeleted;
        _chat.OnRoomRenamed -= HandleRoomRenamed;
        _chat.OnUserKicked -= HandleUserKicked;
        _chat.OnUserMoved -= HandleUserMoved;
        _chat.OnUserRoleChanged -= HandleUserRoleChanged;
        _chat.OnUserProfileUpdated -= HandleUserProfileUpdated;
        _chat.OnUserOnlineStatusChanged -= HandleUserOnlineStatusChanged;
        _chat.OnConnectionStateChanged -= HandleConnectionStateChanged;
    }
}

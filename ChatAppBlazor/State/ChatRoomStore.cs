using ChatAppBlazor.Serialization;
using ChatAppBlazor.Services;
using Microsoft.Maui.Storage;
using Shared;
using Shared.Models;
using System.Text.Json;

namespace ChatAppBlazor.State;

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
    private readonly CancellationTokenSource _serverMembersRefreshCts = new();
    private readonly Task _serverMembersRefreshLoopTask;
    private int _serverMembersRefreshInProgress;
    private Dictionary<Guid, RoomNotificationMode> _roomNotificationModes = [];
    private Guid? _notificationPrefsUserId;
    private readonly TypingTracker _typing = new();
    private readonly Task _typingSweepLoopTask;
    private DateTime _lastTypingSentAt = DateTime.MinValue;
    private CancellationTokenSource? _markReadCts;
    private Guid? _pendingReadRoomId;
    private DateTime _pendingReadAt = DateTime.MinValue;

    /// <summary>Per-room unread badges, keyed by room id. Absent means nothing unread.</summary>
    public Dictionary<Guid, RoomUnreadState> UnreadStates { get; private set; } = [];

    public ChatRoomInfo[] Rooms { get; private set; } = [];
    public Guid? SelectedRoomId { get; private set; }
    public ChatRoomInfo? SelectedRoom { get; private set; }
    public List<ChatMessage> Messages { get; } = [];

    /// <summary>
    /// Bumped whenever a message is mutated in place (edit, delete). The list count
    /// does not change for those, so the render loop's scroll gate needs this to
    /// notice that message heights may have shifted.
    /// </summary>
    public int MessagesVersion { get; private set; }

    /// <summary>False once a history fetch comes back short, i.e. we have reached the start.</summary>
    public bool HasMoreHistory { get; private set; } = true;
    public bool IsLoadingOlder { get; private set; }

    public ChatMemberInfo[] Members { get; private set; } = [];
    public ServerMemberInfo[] ServerMembers { get; private set; } = [];
    private Dictionary<Guid, ServerMemberInfo> _serverMembersById = [];

    /// <summary>
    /// O(1) member lookup. The drawer calls this several times per participant per render, which
    /// was a linear scan of the whole server roster each time.
    /// </summary>
    public ServerMemberInfo? FindServerMember(Guid userId)
        => _serverMembersById.GetValueOrDefault(userId);

    private void SetServerMembers(ServerMemberInfo[] members)
    {
        ServerMembers = members;

        var index = new Dictionary<Guid, ServerMemberInfo>(members.Length);
        foreach (var member in members)
        {
            index[member.UserId] = member;
        }
        _serverMembersById = index;
    }
    public UserRole CurrentUserRole { get; private set; } = UserRole.Member;
    public string? Error { get; private set; }

    private readonly HashSet<Guid> _messageIds = [];
    private int _selectionVersion;
    private int _reconnectVersion;
    private MessageCursor? _disconnectedCursor;
    private bool _needsRecovery;
    private static MessageCursor Position(ChatMessage message) => new() { SentAt = message.SentAt, Id = message.Id };
    private void MergeMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages) _api.NormalizeAttachments(message.Attachments);
        MessageHistory.Merge(Messages, messages, Position, _messageIds);
    }

    public event Action? OnChange;
    /// <summary>Fired when the current user is moved to a new room by a moderator.</summary>
    public event Action<Guid>? OnCurrentUserMoved;

    public ChatRoomStore(ApiClientService api, ChatClientService chat, AuthStateProvider auth)
    {
        _api = api;
        _chat = chat;
        _auth = auth;

        _chat.OnMessageReceived += HandleMessageReceived;
        _chat.OnMessageDeleted += HandleMessageDeleted;
        _chat.OnMessageEdited += HandleMessageEdited;
        _chat.OnMessageReactionChanged += HandleMessageReactionChanged;
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

        _chat.OnTyping += HandleTyping;
        _chat.OnRoomRead += HandleRoomRead;

        _serverMembersRefreshLoopTask = RunServerMembersRefreshLoopAsync(_serverMembersRefreshCts.Token);
        _typingSweepLoopTask = RunTypingSweepLoopAsync(_serverMembersRefreshCts.Token);
    }

    private void HandleTyping(Guid roomId, Guid userId, string userName)
    {
        // Your own composing is not news to you.
        if (roomId != SelectedRoomId || userId == _auth.CurrentUser?.Id) return;

        if (_typing.Touch(userId, userName, DateTime.UtcNow))
        {
            NotifyChanged();
        }
    }

    /// <summary>Null when nobody in the open room is typing.</summary>
    public string? TypingSummary => _typing.Describe(DateTime.UtcNow);

    /// <summary>
    /// Signals composing, at most once per throttle window. Cheap enough to call on
    /// every keystroke.
    /// </summary>
    public async Task NotifyTypingAsync()
    {
        if (SelectedRoomId == null) return;

        var now = DateTime.UtcNow;
        if (now - _lastTypingSentAt < TimeSpan.FromSeconds(ChatLimits.TypingThrottleSeconds)) return;
        _lastTypingSentAt = now;

        try
        {
            await _chat.SetTypingAsync(SelectedRoomId.Value);
        }
        catch
        {
            // A dropped typing signal is not worth surfacing.
        }
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
            await RefreshUnreadStatesAsync();
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

        var selection = ++_selectionVersion;
        var previousRoomId = SelectedRoomId;
        SelectedRoomId = roomId;
        _disconnectedCursor = null;
        _needsRecovery = false;
        IsLoadingOlder = false;
        HasMoreHistory = true;
        SelectedRoom = Rooms.FirstOrDefault(r => r.Id == roomId);
        _typing.Clear();
        Messages.Clear();
        _messageIds.Clear();
        Members = [];
        CurrentUserRole = GetCurrentUserGlobalRole();
        Error = null;
        NotifyChanged();

        // Leave previous room without allowing its completion to restore an old selection.
        if (previousRoomId != null && !keepPreviousRoomSubscription)
        {
            try { await _chat.LeaveRoomAsync(previousRoomId.Value); } catch { }
            if (selection != _selectionVersion || SelectedRoomId != roomId) return;
        }

        try
        {
            await _chat.JoinRoomAsync(roomId);
            if (selection != _selectionVersion || SelectedRoomId != roomId) return;
            var msgs = await _api.GetRoomMessagePageAsync(roomId);
            if (selection != _selectionVersion || SelectedRoomId != roomId) return;

            MergeMessages(msgs);
            // A short first page means the whole room fits in one.
            HasMoreHistory = msgs.Length >= 50;
            // Opening a room reads it.
            UnreadStates.Remove(roomId);
            ScheduleMarkSelectedRoomRead();
            await RefreshSelectedRoomMembersAsync(roomId);
            await RefreshServerMembersAsync();
            NotifyChanged();
        }
        catch (Exception ex)
        {
            if (selection != _selectionVersion || SelectedRoomId != roomId) return;
            Error = $"Failed to join room: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task<bool> SendMessageAsync(string content, AttachmentInfo[]? attachments = null, Guid[]? mentionedUserIds = null, Guid? replyToMessageId = null)
    {
        if (SelectedRoomId == null) return false;
        var hasText = !string.IsNullOrWhiteSpace(content);
        var hasAttachments = attachments is { Length: > 0 };
        if (!hasText && !hasAttachments) return false;

        try
        {
            await _chat.SendMessageAsync(
                SelectedRoomId.Value,
                content.Trim(),
                mentionedUserIds: mentionedUserIds,
                attachments: attachments,
                replyToMessageId: replyToMessageId);
            return true;
        }
        catch (Exception ex)
        {
            Error = $"Failed to send message: {ex.Message}";
            NotifyChanged();
            return false;
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

    public async Task<bool> EditMessageAsync(Guid messageId, string content, Guid[]? mentionedUserIds = null)
    {
        if (SelectedRoomId == null) return false;
        if (string.IsNullOrWhiteSpace(content)) return false;

        try
        {
            await _chat.EditMessageAsync(SelectedRoomId.Value, messageId, content.Trim(), mentionedUserIds);
            return true;
        }
        catch (Exception ex)
        {
            Error = $"Failed to edit message: {ex.Message}";
            NotifyChanged();
            return false;
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
            // The message is the proof they stopped typing; don't wait for expiry.
            _typing.Remove(message.SenderId);
            MergeMessages([message]);
            // Reading the open room includes whatever just landed in it.
            ScheduleMarkSelectedRoomRead();
            NotifyChanged();
            return;
        }

        // Not the open room: badge it, unless it is our own message echoing back.
        var myId = _auth.CurrentUser?.Id;
        if (myId == null || message.SenderId == myId) return;

        if (!UnreadStates.TryGetValue(message.RoomId, out var state))
        {
            state = new RoomUnreadState { RoomId = message.RoomId };
            UnreadStates[message.RoomId] = state;
        }

        state.UnreadCount = Math.Min(state.UnreadCount + 1, 99);
        if (message.MentionedUserIds.Contains(myId.Value) || message.MentionedUserIds.Contains(Guid.Empty))
        {
            state.HasMention = true;
        }

        NotifyChanged();
    }

    private void HandleMessageDeleted(Guid roomId, Guid messageId)
    {
        if (roomId == SelectedRoomId)
        {
            var msg = Messages.FirstOrDefault(m => m.Id == messageId);
            if (msg != null) msg.IsDeleted = true;

            // Quotes are snapshots taken at read time, so loaded replies to this message
            // have to be told their parent just went away.
            foreach (var reply in Messages.Where(m => m.ReplyToMessageId == messageId && m.ReplyPreview != null))
            {
                reply.ReplyPreview!.IsUnavailable = true;
            }

            MessagesVersion++;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Prepends the previous page of history. Returns false when there is nothing older,
    /// so the caller can retire the "load older" affordance.
    /// </summary>
    public async Task<bool> LoadOlderMessagesAsync(int count = 50)
    {
        if (SelectedRoomId is not { } conversationId || Messages.Count == 0 || IsLoadingOlder || !HasMoreHistory) return false;
        count = Math.Clamp(count, 1, 100);
        var selection = _selectionVersion;
        var oldest = Position(Messages[0]);
        IsLoadingOlder = true;
        NotifyChanged();
        try
        {
            var older = await _api.GetRoomMessagePageAsync(conversationId, count, before: oldest);
            if (selection != _selectionVersion || SelectedRoomId != conversationId) return false;
            HasMoreHistory = older.Length >= count;
            var previousCount = Messages.Count;
            MergeMessages(older);
            return Messages.Count != previousCount;
        }
        catch (Exception ex)
        {
            if (selection == _selectionVersion && SelectedRoomId == conversationId) Error = $"Failed to load older messages: {ex.Message}";
            return false;
        }
        finally
        {
            if (selection == _selectionVersion && SelectedRoomId == conversationId)
            {
                IsLoadingOlder = false;
                NotifyChanged();
            }
        }
    }

    private void HandleMessageReactionChanged(Guid roomId, Guid messageId, Guid userId, string emoji, bool added)
    {
        if (roomId != SelectedRoomId) return;

        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg == null) return;

        msg.Reactions = ReactionState.Apply(msg.Reactions, userId, emoji, added);
        MessagesVersion++;
        NotifyChanged();
    }

    public async Task ToggleReactionAsync(Guid messageId, string emoji)
    {
        if (SelectedRoomId == null) return;
        try
        {
            await _chat.ToggleReactionAsync(SelectedRoomId.Value, messageId, emoji);
        }
        catch (Exception ex)
        {
            Error = $"Failed to react: {ex.Message}";
            NotifyChanged();
        }
    }

    private void HandleMessageEdited(Guid roomId, Guid messageId, string content, Guid[] mentionedUserIds, DateTime editedAt)
    {
        if (roomId != SelectedRoomId) return;

        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg != null)
        {
            msg.Content = content;
            msg.MentionedUserIds = mentionedUserIds;
            msg.EditedAt = editedAt;
        }

        MessagesVersion++;
        NotifyChanged();
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
            _messageIds.Clear();
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
            _messageIds.Clear();
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
        var member = FindServerMember(userId);
        if (member == null) return;
        if (member.IsOnline == isOnline) return;

        member.IsOnline = isOnline;
        NotifyChanged();
    }

    private void HandleUserProfileUpdated(UserProfileUpdateInfo profile)
    {
        var changed = false;

        var serverMember = FindServerMember(profile.UserId);
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
        var reconnect = ++_reconnectVersion;
        if (!connected)
        {
            // Capture before live events resume, especially DMs delivered during connection setup.
            // Keep the original gap boundary if the connection drops again mid-recovery.
            if (!_needsRecovery)
                _disconnectedCursor = Messages.Count == 0
                    ? new MessageCursor { SentAt = DateTime.UnixEpoch, Id = Guid.Empty }
                    : Position(Messages[^1]);
            _needsRecovery = true;
            return;
        }
        if (SelectedRoomId is not { } conversationId)
        {
            await RefreshUnreadStatesAsync();
            return;
        }
        var selection = _selectionVersion;
        var after = _needsRecovery ? _disconnectedCursor : (Messages.Count == 0 ? null : Position(Messages[^1]));
        bool IsCurrent() => selection == _selectionVersion && reconnect == _reconnectVersion && SelectedRoomId == conversationId;
        try
        {
            await _chat.JoinRoomAsync(conversationId);
            if (!IsCurrent()) return;
            await MessageHistory.RecoverAsync(after,
                cursor => _api.GetRoomMessagePageAsync(conversationId, after: cursor),
                Position, IsCurrent, page =>
                {
                    MergeMessages(page);
                    NotifyChanged();
                });
            if (!IsCurrent()) return;
            _disconnectedCursor = null;
            _needsRecovery = false;
            Error = null;
            await RefreshRoomsSnapshotAsync();
            if (!IsCurrent()) return;
            await RefreshSelectedRoomMembersAsync(conversationId);
            await RefreshServerMembersAsync();
            await RefreshUnreadStatesAsync();
            if (IsCurrent()) ScheduleMarkSelectedRoomRead();
            if (IsCurrent()) NotifyChanged();
        }
        catch (Exception ex)
        {
            if (!IsCurrent()) return;
            Error = $"Failed to recover messages: {ex.Message}";
            NotifyChanged();
        }
    }

    /// <summary>Replaces local badge state with the server's view.</summary>
    public async Task RefreshUnreadStatesAsync()
    {
        try
        {
            var states = await _api.GetRoomUnreadStatesAsync();
            UnreadStates = states.ToDictionary(s => s.RoomId);

            // The open room is read by definition.
            if (SelectedRoomId is { } openRoomId)
            {
                UnreadStates.Remove(openRoomId);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatRoomStore] Unread refresh failed: {ex.Message}");
        }
    }

    public int GetUnreadCount(Guid roomId)
        => UnreadStates.TryGetValue(roomId, out var state) ? state.UnreadCount : 0;

    public bool HasUnreadMention(Guid roomId)
        => UnreadStates.TryGetValue(roomId, out var state) && state.HasMention;

    private void HandleRoomRead(Guid roomId, DateTime lastReadAt)
    {
        // Another of this user's devices opened the room.
        if (UnreadStates.Remove(roomId))
        {
            NotifyChanged();
        }
    }

    /// <summary>
    /// Reports the open room as read, coalescing bursts. Safe to call on every arriving
    /// message; the server ignores non-advancing cursors anyway.
    /// </summary>
    public void ScheduleMarkSelectedRoomRead()
    {
        if (SelectedRoomId == null || Messages.Count == 0) return;

        var roomId = SelectedRoomId.Value;
        var lastSentAt = Messages[^1].SentAt;
        if (_pendingReadRoomId == roomId && _pendingReadAt >= lastSentAt) return;

        _pendingReadRoomId = roomId;
        _pendingReadAt = lastSentAt;

        _markReadCts?.Cancel();
        _markReadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _markReadCts = cts;

        _ = MarkReadAfterDelayAsync(roomId, lastSentAt, cts.Token);
    }

    private async Task MarkReadAfterDelayAsync(Guid roomId, DateTime lastReadAt, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (SelectedRoomId != roomId) return;
            await _chat.MarkRoomReadAsync(roomId, lastReadAt);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer message.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatRoomStore] Mark-read failed: {ex.Message}");
        }
    }

    private async Task RefreshSelectedRoomMembersAsync(Guid roomId)
    {
        var selection = _selectionVersion;
        var members = await _api.GetRoomMembersAsync(roomId);
        if (selection == _selectionVersion && SelectedRoomId == roomId) Members = members;
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
            SetServerMembers(nextMembers);
            CurrentUserRole = GetCurrentUserGlobalRole();

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

    /// <summary>
    /// Ages out typing indicators. There is no "stopped typing" event, so the only way
    /// an indicator disappears is by expiring here.
    /// </summary>
    private async Task RunTypingSweepLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // Notify only when something actually disappeared.
                if (_typing.Sweep(DateTime.UtcNow))
                {
                    NotifyChanged();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
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
        => (_auth.CurrentUser?.Id is Guid id ? FindServerMember(id)?.Role : null) ?? UserRole.Member;

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
            var raw = Preferences.Default.Get(GetRoomNotificationModesPrefKey(), string.Empty);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var serializedModes = JsonSerializer.Deserialize(raw, AppJsonContext.Default.DictionaryStringInt32) ?? [];
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
            var legacyRaw = Preferences.Default.Get(GetLegacyMutedRoomNotificationsPrefKey(), string.Empty);
            if (string.IsNullOrWhiteSpace(legacyRaw))
            {
                return [];
            }

            var mutedRoomIds = JsonSerializer.Deserialize(legacyRaw, AppJsonContext.Default.GuidArray) ?? [];
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

            Preferences.Default.Set(GetRoomNotificationModesPrefKey(), JsonSerializer.Serialize(payload, AppJsonContext.Default.DictionaryStringInt32));
        }
        catch
        {
            // Ignore preference persistence failures.
        }
    }

    public void Dispose()
    {
        _selectionVersion++;
        _reconnectVersion++;
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

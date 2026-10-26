using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChatApp.Core.Media;
using ChatApp.Core.Services;
using ChatApp.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shared.Models;

namespace ChatApp.Ui.ViewModels;

/// <summary>
/// Signed-in shell: room list, message list, composer.
/// <para>
/// This is the first slice of the <c>MainLayout.razor</c> + <c>ChatRooms.razor</c>
/// port and deliberately covers only text chat. Calls, DMs, members panel,
/// attachments and mentions land as their own view models.
/// </para>
/// </summary>
public partial class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly ChatRoomStore _rooms;
    private readonly AuthStateProvider _auth;
    private readonly CallStore _calls;
    private readonly GrpcChannelProvider _channels;
    private readonly ChatClientService _chat;
    private readonly MediaEngineHost? _media;
    private bool _disposed;

    [ObservableProperty]
    public partial bool IsInCall { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsCameraOn { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string CallStatus { get; set; } = string.Empty;

    /// <summary>
    /// The video grid, built only while a call is up so no tile holds a decoder buffer
    /// between calls.
    /// </summary>
    [ObservableProperty]
    public partial CallViewModel? Call { get; set; }

    /// <summary>Non-null while the settings dialog is open.</summary>
    [ObservableProperty]
    public partial SettingsViewModel? Settings { get; set; }

    [ObservableProperty]
    public partial bool IsScreenSharing { get; set; }

    /// <summary>Non-null while the share-target picker is open.</summary>
    [ObservableProperty]
    public partial ScreenSharePickerViewModel? SharePicker { get; set; }

    /// <summary>Stops an active share, or opens the picker to start one.</summary>
    [RelayCommand]
    private async Task ToggleScreenShareAsync()
    {
        if (_media is null)
            return;

        if (IsScreenSharing)
        {
            IsBusy = true;
            try
            {
                await _media.Engine.StopScreenShareAsync();
                CallStatus = "Stopped sharing.";
            }
            catch (Exception ex)
            {
                CallStatus = $"Stopping the share failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }

            return;
        }

        if (SharePicker is not null)
            return;

        var picker = new ScreenSharePickerViewModel(_media);
        picker.Completed += HandleShareTargetChosen;
        SharePicker = picker;
    }

    private async void HandleShareTargetChosen(ScreenShareTarget? target)
    {
        var picker = SharePicker;
        SharePicker = null;

        if (picker is not null)
            picker.Completed -= HandleShareTargetChosen;

        if (target is null || _media is null)
            return;

        IsBusy = true;
        CallStatus = $"Sharing {target.Title}…";

        try
        {
            await _media.Engine.StartScreenShareAsync(target);
            CallStatus = $"Sharing {target.Title}.";
        }
        catch (Exception ex)
        {
            CallStatus = $"Screen share failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (Settings is not null)
            return;

        var settings = new SettingsViewModel(_calls, _channels, _media);
        settings.Closed += CloseSettings;
        Settings = settings;
    }

    private void CloseSettings()
    {
        var settings = Settings;
        Settings = null;

        if (settings is not null)
        {
            settings.Closed -= CloseSettings;
            settings.Dispose();
        }
    }

    /// <summary>Speaking participants, newest state wins. Drives the indicator dots.</summary>
    public ObservableCollection<string> SpeakingNow { get; } = [];

    public ObservableCollection<ChatRoomInfo> Rooms { get; } = [];
    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];

    [ObservableProperty]
    public partial ChatRoomInfo? SelectedRoom { get; set; }

    [ObservableProperty]
    public partial string Draft { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    public string CurrentUserName => _auth.CurrentUser?.DisplayName ?? "…";

    /// <summary>Direct messages: sidebar contacts plus the open thread.</summary>
    public DmViewModel Dm { get; }

    /// <summary>True while a DM conversation occupies the main pane instead of a room.</summary>
    [ObservableProperty]
    public partial bool IsDmOpen { get; set; }

    // ─── Incoming private call ─────────────────────────────────────────────────

    [ObservableProperty]
    public partial string? IncomingCallerName { get; set; }

    private Guid _incomingCallerId;

    /// <summary>Room and server members, and the door into new DM conversations.</summary>
    public MembersPanelViewModel Members { get; }

    /// <summary>
    /// Who is in the selected room's call, e.g. "In call: Duncan, Ana" — visible before
    /// joining, which is the whole point: nobody joins a call they cannot see exists.
    /// </summary>
    [ObservableProperty]
    public partial string? RoomCallSummary { get; set; }

    /// <summary>Sidebar rooms with their call participants nested underneath.</summary>
    public ObservableCollection<RoomItemViewModel> RoomItems { get; } = [];

    [ObservableProperty]
    public partial RoomItemViewModel? SelectedRoomItem { get; set; }

    partial void OnSelectedRoomItemChanged(RoomItemViewModel? value)
    {
        // Opening a room reads its messages, so its badge retires.
        if (value is not null)
            value.UnreadCount = 0;

        // The wrapper drives the original property so all existing room logic —
        // DM exclusivity, store selection — runs unchanged.
        if (!Equals(SelectedRoom?.Id, value?.Room.Id))
            SelectedRoom = value?.Room;
    }

    /// <summary>
    /// Rebuilds the sidebar tree, reusing room items so selection survives. Participant
    /// rows come from the call-participant snapshot; names resolve via the member list.
    /// </summary>
    private void SyncRoomItems()
    {
        var known = RoomItems.ToDictionary(r => r.Id);
        var seen = new HashSet<Guid>();

        foreach (var room in Rooms)
        {
            seen.Add(room.Id);

            if (!known.TryGetValue(room.Id, out var item))
            {
                item = new RoomItemViewModel(room)
                {
                    RenameCommand = OpenRenameRoomCommand,
                    DeleteCommand = OpenDeleteRoomCommand,
                    NotifyAllCommand = SetRoomNotifyAllCommand,
                    NotifyMentionsCommand = SetRoomNotifyMentionsCommand,
                    NotifyMutedCommand = SetRoomNotifyMutedCommand
                };
                RoomItems.Add(item);
            }
            else
            {
                item.Update(room);
            }

            item.CanManage = room.OwnerId == (_auth.CurrentUser?.Id ?? Guid.Empty)
                || _rooms.CurrentUserRole >= UserRole.Admin;

            SyncRoomParticipants(item);
        }

        for (var i = RoomItems.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(RoomItems[i].Id))
                RoomItems.RemoveAt(i);
        }

        if (SelectedRoomItem is null && _rooms.SelectedRoomId is { } selectedId)
            SelectedRoomItem = RoomItems.FirstOrDefault(r => r.Id == selectedId);
    }

    private void SyncRoomParticipants(RoomItemViewModel item)
    {
        var participants = _calls.GetRoomParticipants(item.Id);
        item.HasActiveCall = participants.Count > 0;

        var actorRole = _rooms.CurrentUserRole;
        var selfId = _auth.CurrentUser?.Id;

        var existing = item.CallParticipants.ToDictionary(p => p.UserId);
        var kept = new HashSet<Guid>();

        foreach (var userId in participants)
        {
            kept.Add(userId);

            if (!existing.TryGetValue(userId, out var participant))
            {
                participant = new CallParticipantItem(userId, item.Id)
                {
                    MoveCommand = MoveParticipantCommand,
                    RemoveFromCallCommand = RemoveParticipantFromCallCommand,
                    SetRoleCommand = SetParticipantRoleCommand
                };
                item.CallParticipants.Add(participant);
            }

            participant.Name = ResolveUserName(userId);

            var isSelf = userId == selfId;
            var targetRole = _rooms.ServerMembers.FirstOrDefault(m => m.UserId == userId)?.Role
                ?? UserRole.Member;

            // Gates lifted verbatim from MainLayout.razor's participant popup:
            // CanModerateRoomParticipant, CanMoveRoomParticipant, GetAssignableRoles.
            participant.CanModerate =
                !isSelf && actorRole >= UserRole.Moderator && targetRole < actorRole;

            participant.CanMove =
                actorRole >= UserRole.Moderator
                && (actorRole >= UserRole.Admin || isSelf || targetRole < actorRole);

            // Move destinations: every other room. Rebuilt in place because the rooms
            // list itself can change between syncs.
            participant.MoveTargets.Clear();
            if (participant.CanMove)
            {
                foreach (var room in Rooms.Where(r => r.Id != item.Id)
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
                    participant.MoveTargets.Add(new MoveTarget($"#{room.Name}", room.Id, participant));
                }
            }

            participant.AssignableRoles.Clear();
            if (participant.CanModerate && actorRole >= UserRole.Admin)
            {
                foreach (var role in Enum.GetValues<UserRole>()
                    .Where(r => r != UserRole.Owner && r < actorRole && r != targetRole)
                    .OrderByDescending(r => r))
                {
                    participant.AssignableRoles.Add(new RoleTarget(role, participant));
                }
            }

            participant.HasActions =
                participant.CanModerate
                || (participant.CanMove && participant.MoveTargets.Count > 0)
                || participant.AssignableRoles.Count > 0;
        }

        for (var i = item.CallParticipants.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(item.CallParticipants[i].UserId))
                item.CallParticipants.RemoveAt(i);
        }
    }

    [RelayCommand]
    private async Task MoveParticipantAsync(MoveTarget? target)
    {
        if (target is null)
            return;

        try
        {
            await _chat.MoveUserAsync(target.Participant.RoomId, target.TargetRoomId, target.Participant.UserId);
        }
        catch (Exception ex)
        {
            CallStatus = $"Could not move the participant: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RemoveParticipantFromCallAsync(CallParticipantItem? participant)
    {
        if (participant is null)
            return;

        try
        {
            await _chat.KickUserFromVoiceVideoAsync(participant.RoomId, participant.UserId, "moderation");
        }
        catch (Exception ex)
        {
            CallStatus = $"Could not remove the participant: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SetParticipantRoleAsync(RoleTarget? target)
    {
        if (target is null)
            return;

        try
        {
            await _chat.SetUserRoleAsync(
                target.Participant.RoomId, target.Participant.UserId, target.Role);
        }
        catch (Exception ex)
        {
            CallStatus = $"Could not change the role: {ex.Message}";
        }
    }

    private readonly ApiClientService _api;
    private readonly DispatcherTimer _callParticipantsTimer;

    /// <summary>
    /// Polls the room-call participant snapshot, replacing the Blazor MainLayout loop.
    /// The hub's join/leave events keep the store live while we listen; the poll heals
    /// missed events and provides the initial state after sign-in.
    /// </summary>
    private async void HandleCallParticipantsTick(object? sender, EventArgs e)
    {
        if (_disposed || !_auth.IsAuthenticated || !_chat.IsConnected)
            return;

        try
        {
            var snapshots = await _api.GetRoomCallParticipantsAsync();
            _calls.ApplyRoomParticipantSnapshot(snapshots);
        }
        catch
        {
            // Transient — the next tick retries, and hub events still flow meanwhile.
        }

        UpdateRoomCallSummary();
        SyncRoomItems();
    }

    private void UpdateRoomCallSummary()
    {
        if (SelectedRoom is not { } room)
        {
            RoomCallSummary = null;
            return;
        }

        var participants = _calls.GetRoomParticipants(room.Id);

        RoomCallSummary = participants.Count == 0
            ? null
            : $"In call: {string.Join(", ", participants.Select(ResolveUserName))}";
    }

    private string ResolveUserName(Guid userId)
    {
        if (userId == _auth.CurrentUser?.Id)
            return "You";

        // Resolved from the store, not the panel's list — the panel narrows to the
        // active call while in one, and the summary may describe a different room.
        var member = _rooms.ServerMembers.FirstOrDefault(m => m.UserId == userId);

        return member is null
            ? "Someone"
            : string.IsNullOrWhiteSpace(member.DisplayName) ? member.UserName : member.DisplayName;
    }

    public ShellViewModel(
        ChatRoomStore rooms,
        AuthStateProvider auth,
        CallStore calls,
        GrpcChannelProvider channels,
        DirectMessageStore dms,
        ChatClientService chat,
        MembersPanelStore membersPanel,
        ApiClientService api,
        MediaEngineHost? media = null)
    {
        _rooms = rooms;
        _auth = auth;
        _calls = calls;
        _channels = channels;
        _chat = chat;
        _api = api;
        _media = media;

        Dm = new DmViewModel(dms, chat, auth, api);
        Dm.SelectionChanged += HandleDmSelectionChanged;
        _chat.OnPrivateCallIncoming += HandleIncomingPrivateCall;

        Members = new MembersPanelViewModel(rooms, membersPanel, auth, chat, calls);
        Members.StartDmRequested += HandleStartDmRequested;

        // Notification routing: badges and a taskbar flash instead of OS toasts —
        // dependency-free and AOT-clean by choice.
        _chat.OnMessageReceived += HandleRoomMessageForNotifications;
        _chat.OnPrivateMessageReceived += HandleDmForNotifications;

        _callParticipantsTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(5), DispatcherPriority.Background, HandleCallParticipantsTick);
        _callParticipantsTimer.Start();

        _rooms.OnChange += HandleStoreChanged;
        _calls.OnChange += HandleCallStoreChanged;

        if (_media is not null)
            _media.SpeakingChanged += HandleSpeakingChanged;

        _ = _rooms.LoadRoomsAsync();
    }

    [RelayCommand]
    private async Task JoinCallAsync()
    {
        if (_media is null)
        {
            CallStatus = "Calls are not available on this platform yet.";
            return;
        }

        if (SelectedRoom is not { } room)
        {
            CallStatus = "Select a room first.";
            return;
        }

        IsBusy = true;
        CallStatus = $"Joining {room.Name}…";

        try
        {
            // The grid subscribes before the engine starts, so the first publisher to
            // appear already has somewhere to land.
            Call ??= new CallViewModel(_media, _auth);

            var joined = await _media.JoinRoomCallAsync(room.Id);
            CallStatus = joined
                ? $"In call: {room.Name}"
                : _calls.Error ?? "Could not join the call.";

            if (!joined)
                DisposeCallView();
        }
        catch (Exception ex)
        {
            CallStatus = $"Call failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LeaveCallAsync()
    {
        if (_media is null)
            return;

        IsBusy = true;
        try
        {
            await _media.LeaveCallAsync();
            CallStatus = "Left the call.";
        }
        catch (Exception ex)
        {
            CallStatus = $"Leaving failed: {ex.Message}";
        }
        finally
        {
            DisposeCallView();
            IsBusy = false;
        }
    }

    private void DisposeCallView()
    {
        var call = Call;
        Call = null;
        call?.Dispose();

        // A picker left open past the end of the call would start a share into nothing.
        if (SharePicker is { } picker)
        {
            SharePicker = null;
            picker.Completed -= HandleShareTargetChosen;
        }
    }

    [RelayCommand]
    private async Task ToggleMuteAsync()
    {
        if (_media is null)
            return;

        await _media.Engine.SetMutedAsync(!IsMuted);
    }

    [RelayCommand]
    private async Task ToggleCameraAsync()
    {
        if (_media is null)
            return;

        IsBusy = true;
        try
        {
            var enable = !IsCameraOn;
            await _media.Engine.SetCameraEnabledAsync(enable);
            IsCameraOn = enable;
            CallStatus = enable ? "Camera on." : "Camera off.";
        }
        catch (Exception ex)
        {
            CallStatus = $"Camera failed: {ex.Message}";
            IsCameraOn = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void HandleSpeakingChanged(Guid userId, bool speaking)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var key = userId.ToString();

            if (speaking)
            {
                if (!SpeakingNow.Contains(key))
                    SpeakingNow.Add(key);
            }
            else
            {
                SpeakingNow.Remove(key);
            }
        });
    }

    private void HandleCallStoreChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsInCall = _calls.IsInCall;
            IsMuted = _calls.IsMuted;
            IsCameraOn = !_calls.IsCameraOff;
            IsScreenSharing = _calls.IsScreenSharing;

            // Join/leave hub events land here between poll ticks.
            UpdateRoomCallSummary();
            SyncRoomItems();

            if (_calls.Error is { Length: > 0 } error)
                CallStatus = error;
        });
    }

    partial void OnSelectedRoomChanged(ChatRoomInfo? value)
    {
        if (value is null)
            return;

        // Picking a room takes over the main pane from any open DM.
        Dm.ClearSelection();

        UpdateRoomCallSummary();

        if (value.Id == _rooms.SelectedRoomId)
            return;

        _ = _rooms.SelectRoomAsync(value.Id);
    }

    private void HandleDmSelectionChanged()
    {
        IsDmOpen = Dm.SelectedContact is not null;

        // And picking a contact releases the room. Null-safe both ways: clearing one
        // side does not re-clear the other, because null assignments return early.
        if (IsDmOpen)
            SelectedRoom = null;
    }

    /// <summary>Starts a private call with the open conversation's contact.</summary>
    [RelayCommand]
    private async Task StartDirectCallAsync()
    {
        if (_media is null || Dm.SelectedContact is not { } contact)
            return;

        IsBusy = true;
        CallStatus = $"Calling {contact.Name}…";

        try
        {
            Call ??= new CallViewModel(_media, _auth);

            var joined = await _media.JoinDirectCallAsync(contact.Id);
            CallStatus = joined ? $"In call with {contact.Name}" : _calls.Error ?? "Could not start the call.";

            if (!joined)
                DisposeCallView();
        }
        catch (Exception ex)
        {
            CallStatus = $"Call failed: {ex.Message}";
            DisposeCallView();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void HandleIncomingPrivateCall(Guid callerUserId) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed)
            return;

        _incomingCallerId = callerUserId;

        IncomingCallerName = Dm.Contacts.FirstOrDefault(c => c.Id == callerUserId)?.Name
            ?? "Someone";

        // A ringing call flashes regardless of message-notification settings.
        WindowAlert.Flash();
    });

    [RelayCommand]
    private async Task AcceptIncomingCallAsync()
    {
        var callerId = _incomingCallerId;

        IncomingCallerName = null;
        _incomingCallerId = Guid.Empty;

        if (_media is null || callerId == Guid.Empty)
            return;

        IsBusy = true;
        try
        {
            // Accepting while already in a call means switching calls, not stacking them.
            if (IsInCall)
                await _media.LeaveCallAsync();

            Dm.SelectById(callerId);

            Call ??= new CallViewModel(_media, _auth);

            var joined = await _media.JoinDirectCallAsync(callerId);
            if (!joined)
            {
                CallStatus = _calls.Error ?? "Could not join the call.";
                DisposeCallView();
            }
        }
        catch (Exception ex)
        {
            CallStatus = $"Joining failed: {ex.Message}";
            DisposeCallView();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DeclineIncomingCall()
    {
        // No reject signal exists in the hub contract; the caller's ring simply goes
        // unanswered, matching the Blazor client.
        IncomingCallerName = null;
        _incomingCallerId = Guid.Empty;
    }

    /// <summary>
    /// Room-message notifications, honoring the per-room mode the store persists:
    /// All flashes on anything, MentionsOnly flashes only when the message mentions
    /// you, Muted never flashes and never counts a badge.
    /// </summary>
    private void HandleRoomMessageForNotifications(ChatMessage message) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || message.SenderId == _auth.CurrentUser?.Id)
            return;

        var mode = _rooms.GetRoomNotificationMode(message.RoomId);
        if (mode == RoomNotificationMode.Muted)
            return;

        // Badge for any non-muted room that is not on screen.
        if (message.RoomId != _rooms.SelectedRoomId || IsDmOpen)
        {
            var item = RoomItems.FirstOrDefault(r => r.Id == message.RoomId);
            if (item is not null)
                item.UnreadCount++;
        }

        if (!_calls.NotificationsEnabled)
            return;

        var mentionsMe = _auth.CurrentUser?.Id is { } me && message.MentionedUserIds.Contains(me);
        if (mode == RoomNotificationMode.MentionsOnly && !mentionsMe)
            return;

        WindowAlert.Flash();
    });

    private void HandleDmForNotifications(PrivateMessage message) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || message.SenderId == _auth.CurrentUser?.Id)
            return;

        if (!_calls.NotificationsEnabled || Dm.IsContactMuted(message.SenderId))
            return;

        WindowAlert.Flash();
    });

    [RelayCommand]
    private void SetRoomNotifyAll(RoomItemViewModel? room)
        => SetRoomNotificationMode(room, RoomNotificationMode.All);

    [RelayCommand]
    private void SetRoomNotifyMentions(RoomItemViewModel? room)
        => SetRoomNotificationMode(room, RoomNotificationMode.MentionsOnly);

    [RelayCommand]
    private void SetRoomNotifyMuted(RoomItemViewModel? room)
        => SetRoomNotificationMode(room, RoomNotificationMode.Muted);

    private void SetRoomNotificationMode(RoomItemViewModel? room, RoomNotificationMode mode)
    {
        if (room is null)
            return;

        _rooms.SetRoomNotificationMode(room.Id, mode);

        // Muting also retires the badge — "leave me alone" includes the counter.
        if (mode == RoomNotificationMode.Muted)
            room.UnreadCount = 0;
    }

    private async void HandleStartDmRequested(UserInfo user)
    {
        try
        {
            await Dm.OpenConversationAsync(user);
        }
        catch (Exception ex)
        {
            CallStatus = $"Could not open the conversation: {ex.Message}";
        }
    }

    /// <summary>Uploaded files waiting in the room composer.</summary>
    public ObservableCollection<PendingAttachment> PendingAttachments { get; } = [];

    [ObservableProperty]
    public partial bool IsUploading { get; set; }

    [RelayCommand]
    private async Task AttachFileAsync()
    {
        IsUploading = true;
        try
        {
            foreach (var pending in await AttachmentUpload.PickAndUploadAsync(_api))
                PendingAttachments.Add(pending);
        }
        catch (Exception ex)
        {
            CallStatus = $"Upload failed: {ex.Message}";
        }
        finally
        {
            IsUploading = false;
        }
    }

    [RelayCommand]
    private void RemovePendingAttachment(PendingAttachment? pending)
    {
        if (pending is not null)
            PendingAttachments.Remove(pending);
    }

    // ─── Mentions ──────────────────────────────────────────────────────────────

    /// <summary>Room members matching the @token currently being typed.</summary>
    public ObservableCollection<MentionCandidate> MentionCandidates { get; } = [];

    [ObservableProperty]
    public partial bool IsMentionPopupOpen { get; set; }

    /// <summary>
    /// Filters candidates for the token after an <c>@</c>. Called by the composer's
    /// code-behind, which owns caret tracking and text insertion — the view model only
    /// decides who matches.
    /// </summary>
    public void UpdateMentionQuery(string? token)
    {
        MentionCandidates.Clear();

        if (token is null)
        {
            IsMentionPopupOpen = false;
            return;
        }

        foreach (var member in _rooms.Members
            .Where(m => m.UserId != _auth.CurrentUser?.Id)
            .Where(m => m.UserName.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                || m.DisplayName.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.UserName, StringComparer.OrdinalIgnoreCase)
            .Take(6))
        {
            MentionCandidates.Add(new MentionCandidate(member.UserId, member.UserName, member.DisplayName));
        }

        IsMentionPopupOpen = MentionCandidates.Count > 0;
    }

    /// <summary>
    /// Resolves @username tokens in the outgoing text against the room's members.
    /// Usernames are the stable handle; display names can contain spaces and collide.
    /// </summary>
    private Guid[] ExtractMentionedUserIds(string text)
    {
        if (!text.Contains('@'))
            return [];

        var ids = new HashSet<Guid>();

        foreach (System.Text.RegularExpressions.Match match in
            System.Text.RegularExpressions.Regex.Matches(text, @"@(\w+)"))
        {
            var userName = match.Groups[1].Value;
            var member = _rooms.Members.FirstOrDefault(
                m => string.Equals(m.UserName, userName, StringComparison.OrdinalIgnoreCase));

            if (member is not null)
                ids.Add(member.UserId);
        }

        return [.. ids];
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        var attachments = PendingAttachments.Select(p => p.Attachment).ToArray();

        if ((text.Length == 0 && attachments.Length == 0) || _rooms.SelectedRoomId is null)
            return;

        Draft = string.Empty;
        PendingAttachments.Clear();
        IsMentionPopupOpen = false;

        await _rooms.SendMessageAsync(
            text,
            attachments.Length > 0 ? attachments : null,
            ExtractMentionedUserIds(text) is { Length: > 0 } mentions ? mentions : null);
    }

    [RelayCommand]
    private void SignOut() => _auth.ClearAuth();

    /// <summary>
    /// Store events arrive on background threads (hub callbacks, refresh loops), so
    /// every collection mutation is marshalled to the UI thread.
    /// </summary>
    private void HandleStoreChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            SyncFromStore();
            return;
        }

        Dispatcher.UIThread.Post(SyncFromStore);
    }

    private void SyncFromStore()
    {
        if (_disposed) return;

        Error = _rooms.Error;

        Rooms.Clear();
        foreach (var room in _rooms.Rooms)
            Rooms.Add(room);

        SyncRoomItems();

        if (_rooms.SelectedRoomId is { } selectedId
            && (SelectedRoom is null || SelectedRoom.Id != selectedId))
        {
            SelectedRoom = _rooms.SelectedRoom;
            SelectedRoomItem = RoomItems.FirstOrDefault(r => r.Id == selectedId);
        }

        SyncMessages();

        OnPropertyChanged(nameof(CurrentUserName));

        // ChatRoomInfo has no change notification and the rename handler mutates it in
        // place, so bindings through SelectedRoom (the header title) are re-evaluated
        // wholesale on every store change.
        OnPropertyChanged(nameof(SelectedRoom));
    }

    /// <summary>
    /// Wrappers cached by message id: attachments hold downloaded bitmaps and link
    /// previews hold fetched metadata, so rebuilding them per store change would repeat
    /// that network work every time any message arrives.
    /// </summary>
    private readonly Dictionary<Guid, MessageItemViewModel> _messageCache = [];

    private void SyncMessages()
    {
        // Sender may always delete their own message; Moderator+ may delete anyone's —
        // the same rule ChatHubImpl.DeleteMessageAsync enforces.
        var selfId = _auth.CurrentUser?.Id;
        var canModerateMessages = _rooms.CurrentUserRole >= UserRole.Moderator;

        Messages.Clear();
        var seen = new HashSet<Guid>();

        foreach (var message in _rooms.Messages)
        {
            seen.Add(message.Id);

            if (!_messageCache.TryGetValue(message.Id, out var item))
            {
                item = new MessageItemViewModel(
                    message,
                    canDelete: message.SenderId == selfId || canModerateMessages,
                    isMentioningMe: selfId is { } me && message.MentionedUserIds.Contains(me),
                    DeleteMessageCommand,
                    message.Attachments.Select(a => new AttachmentItemViewModel(a, _api)).ToArray())
                {
                    LinkPreview = LinkPreviewCardViewModel.TryCreate(message.Content, _api)
                };

                _messageCache[message.Id] = item;
            }
            else
            {
                item.CanDelete = message.SenderId == selfId || canModerateMessages;
            }

            Messages.Add(item);
        }

        // Deleted messages and room switches leave dead entries behind.
        foreach (var id in _messageCache.Keys.Where(id => !seen.Contains(id)).ToArray())
            _messageCache.Remove(id);
    }

    [RelayCommand]
    private async Task DeleteMessageAsync(MessageItemViewModel? item)
    {
        if (item is null)
            return;

        try
        {
            await _rooms.DeleteMessageAsync(item.Message.Id);
        }
        catch (Exception ex)
        {
            CallStatus = $"Could not delete the message: {ex.Message}";
        }
    }

    // ─── Room management ───────────────────────────────────────────────────────
    // One shared dialog covers create, rename and delete-confirm; which one it is
    // shows in the title and whether the text field appears.

    [ObservableProperty]
    public partial bool IsRoomDialogOpen { get; set; }

    [ObservableProperty]
    public partial string RoomDialogTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RoomDialogText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RoomDialogHasInput { get; set; }

    [ObservableProperty]
    public partial string RoomDialogConfirmLabel { get; set; } = "OK";

    private enum RoomDialogKind { None, Create, Rename, Delete }

    private RoomDialogKind _roomDialogKind;
    private RoomItemViewModel? _roomDialogTarget;

    [RelayCommand]
    private void OpenCreateRoom()
    {
        _roomDialogKind = RoomDialogKind.Create;
        _roomDialogTarget = null;
        RoomDialogTitle = "Create a room";
        RoomDialogText = string.Empty;
        RoomDialogHasInput = true;
        RoomDialogConfirmLabel = "Create";
        IsRoomDialogOpen = true;
    }

    [RelayCommand]
    private void OpenRenameRoom(RoomItemViewModel? room)
    {
        if (room is null)
            return;

        _roomDialogKind = RoomDialogKind.Rename;
        _roomDialogTarget = room;
        RoomDialogTitle = $"Rename {room.Name}";
        RoomDialogText = room.Name;
        RoomDialogHasInput = true;
        RoomDialogConfirmLabel = "Rename";
        IsRoomDialogOpen = true;
    }

    [RelayCommand]
    private void OpenDeleteRoom(RoomItemViewModel? room)
    {
        if (room is null)
            return;

        _roomDialogKind = RoomDialogKind.Delete;
        _roomDialogTarget = room;
        RoomDialogTitle = $"Delete {room.Name}? This removes the room for everyone.";
        RoomDialogText = string.Empty;
        RoomDialogHasInput = false;
        RoomDialogConfirmLabel = "Delete";
        IsRoomDialogOpen = true;
    }

    [RelayCommand]
    private void CancelRoomDialog()
    {
        IsRoomDialogOpen = false;
        _roomDialogKind = RoomDialogKind.None;
        _roomDialogTarget = null;
    }

    [RelayCommand]
    private async Task ConfirmRoomDialogAsync()
    {
        var kind = _roomDialogKind;
        var target = _roomDialogTarget;
        var text = RoomDialogText.Trim();

        if (kind is RoomDialogKind.Create or RoomDialogKind.Rename && text.Length == 0)
            return;

        CancelRoomDialog();

        try
        {
            switch (kind)
            {
                case RoomDialogKind.Create:
                    // Voice and video default on: every room in this app is callable,
                    // matching what the Blazor client creates.
                    await _rooms.CreateRoomAsync(text, description: null, voiceEnabled: true, videoEnabled: true);
                    break;

                case RoomDialogKind.Rename when target is not null:
                    await _rooms.RenameRoomAsync(target.Id, text);
                    break;

                case RoomDialogKind.Delete when target is not null:
                    await _rooms.DeleteRoomAsync(target.Id);
                    break;
            }
        }
        catch (Exception ex)
        {
            CallStatus = $"Room change failed: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _callParticipantsTimer.Stop();
        _rooms.OnChange -= HandleStoreChanged;
        _calls.OnChange -= HandleCallStoreChanged;
        _chat.OnPrivateCallIncoming -= HandleIncomingPrivateCall;
        _chat.OnMessageReceived -= HandleRoomMessageForNotifications;
        _chat.OnPrivateMessageReceived -= HandleDmForNotifications;

        if (_media is not null)
            _media.SpeakingChanged -= HandleSpeakingChanged;

        Dm.SelectionChanged -= HandleDmSelectionChanged;
        Dm.Dispose();

        Members.StartDmRequested -= HandleStartDmRequested;
        Members.Dispose();

        DisposeCallView();
        CloseSettings();

        GC.SuppressFinalize(this);
    }
}

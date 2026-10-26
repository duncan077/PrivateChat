using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChatApp.Core.Services;
using ChatApp.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shared.Models;

namespace ChatApp.Ui.ViewModels;

/// <summary>One person in the members panel.</summary>
public sealed record MemberItem(
    Guid UserId,
    string UserName,
    string DisplayName,
    string? AvatarUrl,
    bool IsOnline,
    UserRole Role,
    bool IsSelf,
    bool IsInSelectedRoom,
    UserRole ActorRole)
{
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName;

    /// <summary>Role tag next to the name; plain members carry none.</summary>
    public string RoleLabel => Role switch
    {
        UserRole.Owner => "owner",
        UserRole.Admin => "admin",
        UserRole.Moderator => "mod",
        _ => string.Empty
    };

    public bool HasRoleLabel => Role != UserRole.Member;

    /// <summary>You cannot DM yourself, so the button hides on your own row.</summary>
    public bool CanMessage => !IsSelf;

    // ─── Moderation gates, mirroring ChatHubImpl exactly ───────────────────────
    // Kick: actor ≥ Moderator, target strictly below the actor, and the target must
    // actually be in the selected room — the server 404s otherwise.
    // Roles: actor ≥ Admin, target strictly below, new role strictly below, never Owner.
    // The server enforces all of this again; these only decide what the menu offers.

    private bool OutranksTarget => !IsSelf && Role < ActorRole;

    public bool CanKick => ActorRole >= UserRole.Moderator && OutranksTarget && IsInSelectedRoom;

    public bool CanKickFromCall => CanKick;

    public bool CanSetRole => ActorRole >= UserRole.Admin && OutranksTarget;

    public bool CanMakeMember => CanSetRole && Role != UserRole.Member;

    public bool CanMakeModerator
        => CanSetRole && Role != UserRole.Moderator && UserRole.Moderator < ActorRole;

    public bool CanMakeAdmin
        => CanSetRole && Role != UserRole.Admin && UserRole.Admin < ActorRole;

    public bool HasModeration => CanKick || CanSetRole;

    // Flyout popups sit outside the visual tree, so their menu items cannot reach the
    // panel through ancestor bindings — the commands ride on the row instead, assigned
    // when the list is built.
    public IRelayCommand<MemberItem>? KickCommand { get; init; }
    public IRelayCommand<MemberItem>? KickFromCallCommand { get; init; }
    public IRelayCommand<MemberItem>? MakeMemberCommand { get; init; }
    public IRelayCommand<MemberItem>? MakeModeratorCommand { get; init; }
    public IRelayCommand<MemberItem>? MakeAdminCommand { get; init; }

    public UserInfo ToUserInfo() => new()
    {
        Id = UserId,
        UserName = UserName,
        DisplayName = DisplayName,
        AvatarUrl = AvatarUrl,
        IsOnline = IsOnline
    };
}

/// <summary>
/// The members panel, matching the Blazor app's shape: one flat, alphabetical list of
/// every server member. Room membership is a per-row detail, not a grouping — and
/// during a call the list narrows to the people connected to it unless "Show all" is
/// on, which is how the Blazor panel doubles as the call roster.
/// </summary>
public partial class MembersPanelViewModel : ViewModelBase, IDisposable
{
    private readonly ChatRoomStore _rooms;
    private readonly MembersPanelStore _panel;
    private readonly AuthStateProvider _auth;
    private readonly ChatClientService _chat;
    private readonly CallStore _calls;
    private bool _disposed;

    public ObservableCollection<MemberItem> Members { get; } = [];

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    public partial bool HasRoomSelected { get; set; }

    [ObservableProperty]
    public partial string? SelectedRoomName { get; set; }

    /// <summary>True while a call narrows the list, so the filter row is worth showing.</summary>
    [ObservableProperty]
    public partial bool IsInCall { get; set; }

    /// <summary>The Blazor "Show all" switch: lifts the in-call narrowing.</summary>
    [ObservableProperty]
    public partial bool ShowAllDuringCall { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Raised when the user wants to message someone.</summary>
    public event Action<UserInfo>? StartDmRequested;

    public MembersPanelViewModel(
        ChatRoomStore rooms,
        MembersPanelStore panel,
        AuthStateProvider auth,
        ChatClientService chat,
        CallStore calls)
    {
        _rooms = rooms;
        _panel = panel;
        _auth = auth;
        _chat = chat;
        _calls = calls;

        _rooms.OnChange += HandleStoreChanged;
        _calls.OnChange += HandleStoreChanged;
        _panel.OnChange += HandlePanelChanged;

        IsVisible = _panel.IsVisible;
        SyncFromStore();
    }

    partial void OnShowAllDuringCallChanged(bool value) => SyncFromStore();

    [RelayCommand]
    private void Toggle() => _panel.Toggle();

    [RelayCommand]
    private void Message(MemberItem member) => StartDmRequested?.Invoke(member.ToUserInfo());

    // ─── Moderation ────────────────────────────────────────────────────────────
    // Room membership changes come back through the hub events the store already
    // handles, so nothing here updates lists — the sync does when the server confirms.

    [RelayCommand]
    private Task KickAsync(MemberItem member)
        => RunModerationAsync($"kick {member.Name}", roomId =>
            _chat.KickUserAsync(roomId, member.UserId));

    [RelayCommand]
    private Task KickFromCallAsync(MemberItem member)
        => RunModerationAsync($"remove {member.Name} from the call", roomId =>
            _chat.KickUserFromVoiceVideoAsync(roomId, member.UserId, "moderation"));

    [RelayCommand]
    private Task MakeMemberAsync(MemberItem member)
        => RunModerationAsync($"demote {member.Name}", roomId =>
            _chat.SetUserRoleAsync(roomId, member.UserId, UserRole.Member));

    [RelayCommand]
    private Task MakeModeratorAsync(MemberItem member)
        => RunModerationAsync($"make {member.Name} a moderator", roomId =>
            _chat.SetUserRoleAsync(roomId, member.UserId, UserRole.Moderator));

    [RelayCommand]
    private Task MakeAdminAsync(MemberItem member)
        => RunModerationAsync($"make {member.Name} an admin", roomId =>
            _chat.SetUserRoleAsync(roomId, member.UserId, UserRole.Admin));

    private async Task RunModerationAsync(string what, Func<Guid, Task> action)
    {
        if (_rooms.SelectedRoomId is not { } roomId)
            return;

        Error = null;

        try
        {
            await action(roomId);
        }
        catch (Exception ex)
        {
            // Typically a permission rejection from the server — the gates here and
            // there can disagree when a role changed mid-menu.
            Error = $"Could not {what}: {Shorten(ex.Message)}";
        }
    }

    /// <summary>gRPC exception messages embed the full status; keep the readable part.</summary>
    private static string Shorten(string message)
    {
        var detailIndex = message.IndexOf("Detail=\"", StringComparison.Ordinal);
        if (detailIndex < 0)
            return message;

        var start = detailIndex + "Detail=\"".Length;
        var end = message.IndexOf('"', start);
        return end > start ? message[start..end] : message;
    }

    private void HandlePanelChanged() => OnUiThread(() => IsVisible = _panel.IsVisible);

    private void HandleStoreChanged() => OnUiThread(SyncFromStore);

    private void SyncFromStore()
    {
        if (_disposed)
            return;

        var selfId = _auth.CurrentUser?.Id ?? Guid.Empty;
        var actorRole = _rooms.CurrentUserRole;

        HasRoomSelected = _rooms.SelectedRoomId is not null;
        SelectedRoomName = _rooms.SelectedRoom?.Name;
        IsInCall = _calls.IsInCall;

        var inRoom = _rooms.Members.Select(m => m.UserId).ToHashSet();

        var members = _rooms.ServerMembers.AsEnumerable();

        // In a call the panel narrows to the call roster, exactly like the Blazor
        // switch. Self stays visible — you are in your own call.
        if (IsInCall && !ShowAllDuringCall && _calls.ActiveRoomId is { } activeRoom)
        {
            members = members.Where(m =>
                m.UserId == selfId || _calls.IsUserConnectedInRoom(activeRoom, m.UserId));
        }

        Members.Clear();

        foreach (var member in members
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.UserName, StringComparer.OrdinalIgnoreCase))
        {
            Members.Add(new MemberItem(
                member.UserId,
                member.UserName,
                member.DisplayName,
                member.AvatarUrl,
                member.IsOnline,
                member.Role,
                member.UserId == selfId,
                inRoom.Contains(member.UserId),
                actorRole)
            {
                KickCommand = KickCommand,
                KickFromCallCommand = KickFromCallCommand,
                MakeMemberCommand = MakeMemberCommand,
                MakeModeratorCommand = MakeModeratorCommand,
                MakeAdminCommand = MakeAdminCommand
            });
        }
    }

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _rooms.OnChange -= HandleStoreChanged;
        _calls.OnChange -= HandleStoreChanged;
        _panel.OnChange -= HandlePanelChanged;

        GC.SuppressFinalize(this);
    }
}

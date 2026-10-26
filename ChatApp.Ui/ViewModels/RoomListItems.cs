using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shared.Models;

namespace ChatApp.Ui.ViewModels;

/// <summary>
/// One room in the sidebar, with the people connected to its call nested under it —
/// the Blazor presentation: participants live in the room tree, not in a header line.
/// </summary>
public partial class RoomItemViewModel : ObservableObject
{
    public ChatRoomInfo Room { get; private set; }

    public Guid Id => Room.Id;
    public string Name => Room.Name;

    /// <summary>Last name this item raised a change for — see <see cref="Update"/>.</summary>
    private string _renderedName;

    /// <summary>
    /// Adopts the current snapshot. The change test is against the name last rendered,
    /// not the incoming object: the store's rename handler mutates the same
    /// ChatRoomInfo instance this item already holds, so comparing Room.Name against
    /// room.Name compares an object with itself and never fires.
    /// </summary>
    public void Update(ChatRoomInfo room)
    {
        Room = room;

        if (_renderedName != room.Name)
        {
            _renderedName = room.Name;
            OnPropertyChanged(nameof(Name));
        }
    }

    public ObservableCollection<CallParticipantItem> CallParticipants { get; } = [];

    [ObservableProperty]
    public partial bool HasActiveCall { get; set; }

    /// <summary>Server rule: the room's owner, or a global Admin+, may rename/delete.</summary>
    [ObservableProperty]
    public partial bool CanManage { get; set; }

    /// <summary>
    /// Messages received for this room while another one was open. Session-scoped, like
    /// the DM badges — the server keeps no unread counter.
    /// </summary>
    [ObservableProperty]
    public partial int UnreadCount { get; set; }

    public bool HasUnread => UnreadCount > 0;

    partial void OnUnreadCountChanged(int value) => OnPropertyChanged(nameof(HasUnread));

    public IRelayCommand<RoomItemViewModel>? RenameCommand { get; set; }
    public IRelayCommand<RoomItemViewModel>? DeleteCommand { get; set; }

    // Per-room notification mode, persisted by ChatRoomStore.
    public IRelayCommand<RoomItemViewModel>? NotifyAllCommand { get; set; }
    public IRelayCommand<RoomItemViewModel>? NotifyMentionsCommand { get; set; }
    public IRelayCommand<RoomItemViewModel>? NotifyMutedCommand { get; set; }

    public RoomItemViewModel(ChatRoomInfo room)
    {
        Room = room;
        _renderedName = room.Name;
    }
}

/// <summary>
/// One room message, wrapped so per-row state and commands exist — flyout menu items
/// cannot reach the shell through the visual tree, and the raw shared model cannot
/// carry either.
/// <para>
/// A class cached by message id, not a rebuilt-per-sync record: attachments download
/// their bitmaps and link previews fetch remote metadata, and rebuilding wrappers on
/// every store change repeated all of that network work per new message.
/// </para>
/// </summary>
public sealed partial class MessageItemViewModel : ObservableObject
{
    public ChatMessage Message { get; }
    public IReadOnlyList<AttachmentItemViewModel> Attachments { get; }
    public IRelayCommand<MessageItemViewModel>? DeleteCommand { get; }

    /// <summary>Recomputed on reuse — the actor's role can change mid-session.</summary>
    [ObservableProperty]
    public partial bool CanDelete { get; set; }

    /// <summary>Highlights messages that mention the current user.</summary>
    public bool IsMentioningMe { get; }

    [ObservableProperty]
    public partial LinkPreviewCardViewModel? LinkPreview { get; set; }

    public string SenderName => Message.SenderName;
    public string Content => Message.Content;
    public DateTime SentAt => Message.SentAt;

    public bool HasContent => !string.IsNullOrWhiteSpace(Message.Content);
    public bool HasAttachments => Attachments.Count > 0;

    public MessageItemViewModel(
        ChatMessage message,
        bool canDelete,
        bool isMentioningMe,
        IRelayCommand<MessageItemViewModel>? deleteCommand,
        IReadOnlyList<AttachmentItemViewModel> attachments)
    {
        Message = message;
        CanDelete = canDelete;
        IsMentioningMe = isMentioningMe;
        DeleteCommand = deleteCommand;
        Attachments = attachments;
    }
}

/// <summary>A move destination offered in a participant's moderation flyout.</summary>
public sealed record MoveTarget(string TargetName, Guid TargetRoomId, CallParticipantItem Participant)
{
    /// <summary>
    /// Carried on the record because flyout items cannot reliably reach ancestors
    /// through the visual tree; the shell assigns it when the target list is built.
    /// </summary>
    public IRelayCommand<MoveTarget>? Command => Participant.MoveCommand;
}

/// <summary>One row in the composer's @mention autocomplete.</summary>
public sealed record MentionCandidate(Guid UserId, string UserName, string DisplayName)
{
    public string Handle => $"@{UserName}";

    public string Label => string.IsNullOrWhiteSpace(DisplayName) || DisplayName == UserName
        ? string.Empty
        : DisplayName;
}

/// <summary>A role button in a participant's moderation flyout.</summary>
public sealed record RoleTarget(UserRole Role, CallParticipantItem Participant)
{
    public string Label => Role.ToString();

    public IRelayCommand<RoleTarget>? Command => Participant.SetRoleCommand;
}

/// <summary>One person connected to a room's call.</summary>
public partial class CallParticipantItem : ObservableObject
{
    public Guid UserId { get; }
    public Guid RoomId { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";

    // Gate split mirrors the Blazor sidebar popup exactly: kicking requires outranking
    // the target, while MOVING is looser — Admin and above may move anyone, Owner
    // included, and anyone may move themselves. Role buttons appear per the assignable
    // list. The entry point shows when any action is available.

    /// <summary>Kick gate: actor ≥ Moderator and strictly outranks the target.</summary>
    [ObservableProperty]
    public partial bool CanModerate { get; set; }

    /// <summary>Move gate: Admin+ moves anyone; self-moves allowed; mods need rank.</summary>
    [ObservableProperty]
    public partial bool CanMove { get; set; }

    [ObservableProperty]
    public partial bool HasActions { get; set; }

    /// <summary>Rooms this participant can be moved to, rebuilt when rooms change.</summary>
    public ObservableCollection<MoveTarget> MoveTargets { get; } = [];

    /// <summary>Roles the actor may assign, highest first; empty hides the section.</summary>
    public ObservableCollection<RoleTarget> AssignableRoles { get; } = [];

    /// <summary>Assigned by the shell so flyout items can invoke it without tree walks.</summary>
    public IRelayCommand<MoveTarget>? MoveCommand { get; set; }

    public IRelayCommand<CallParticipantItem>? RemoveFromCallCommand { get; set; }

    public IRelayCommand<RoleTarget>? SetRoleCommand { get; set; }

    public CallParticipantItem(Guid userId, Guid roomId)
    {
        UserId = userId;
        RoomId = roomId;
    }
}

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

/// <summary>One contact in the sidebar.</summary>
public partial class ContactItemViewModel : ObservableObject
{
    public UserInfo User { get; }

    public Guid Id => User.Id;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial bool IsOnline { get; set; }

    /// <summary>
    /// Messages received while the conversation was not open. Session-scoped: the
    /// server keeps no unread counter, so this starts at zero each launch.
    /// </summary>
    [ObservableProperty]
    public partial int UnreadCount { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    public bool HasUnread => UnreadCount > 0;

    partial void OnUnreadCountChanged(int value) => OnPropertyChanged(nameof(HasUnread));

    /// <summary>Assigned by the owning view model so the row's flyout can invoke it.</summary>
    public IRelayCommand<ContactItemViewModel>? MuteCommand { get; set; }

    public ContactItemViewModel(UserInfo user)
    {
        User = user;
        Name = string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName;
        IsOnline = user.IsOnline;
    }
}

/// <summary>One message row, shaped for the template.</summary>
public sealed record DmMessageItem(
    PrivateMessage Message,
    bool IsMine,
    IReadOnlyList<AttachmentItemViewModel> Attachments)
{
    public string SenderName => Message.SenderName;
    public string Content => Message.Content;
    public DateTime SentAt => Message.SentAt;

    public bool HasContent => !string.IsNullOrWhiteSpace(Message.Content);
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Read receipt, shown on our own messages only.</summary>
    public bool ShowRead => IsMine && Message.IsRead;
}

/// <summary>
/// Direct messages: the contact list and the open conversation. Port of the
/// <c>DirectMessages.razor</c> surface over the already-ported
/// <see cref="DirectMessageStore"/>.
/// </summary>
public partial class DmViewModel : ViewModelBase, IDisposable
{
    private readonly DirectMessageStore _dms;
    private readonly ChatClientService _chat;
    private readonly AuthStateProvider _auth;
    private readonly ApiClientService _api;
    private bool _suppressSelection;
    private bool _disposed;
    private readonly Dictionary<Guid, DmMessageItem> _messageCache = [];

    public ObservableCollection<ContactItemViewModel> Contacts { get; } = [];
    public ObservableCollection<DmMessageItem> Messages { get; } = [];

    [ObservableProperty]
    public partial ContactItemViewModel? SelectedContact { get; set; }

    [ObservableProperty]
    public partial string Draft { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Raised after the selection changes for any reason, including clearing.</summary>
    public event Action? SelectionChanged;

    public DmViewModel(
        DirectMessageStore dms,
        ChatClientService chat,
        AuthStateProvider auth,
        ApiClientService api)
    {
        _dms = dms;
        _chat = chat;
        _auth = auth;
        _api = api;

        _dms.OnChange += HandleStoreChanged;
        _chat.OnPrivateMessageReceived += HandlePrivateMessage;
        _chat.OnUserOnlineStatusChanged += HandleOnlineStatus;

        _ = _dms.LoadContactsAsync();
    }

    partial void OnSelectedContactChanged(ContactItemViewModel? value)
    {
        if (_suppressSelection)
            return;

        if (value is not null)
        {
            value.UnreadCount = 0;
            _ = _dms.SelectContactAsync(value.User);
        }

        SelectionChanged?.Invoke();
    }

    /// <summary>Whether DM notifications from this user are muted.</summary>
    public bool IsContactMuted(Guid userId) => _dms.IsContactNotificationsMuted(userId);

    /// <summary>Clears the open conversation without re-entering the selection logic.</summary>
    public void ClearSelection()
    {
        if (SelectedContact is null)
            return;

        _suppressSelection = true;
        SelectedContact = null;
        _suppressSelection = false;

        Messages.Clear();
        SelectionChanged?.Invoke();
    }

    /// <summary>Opens the conversation with a user, e.g. from an accepted incoming call.</summary>
    public void SelectById(Guid userId)
    {
        var contact = Contacts.FirstOrDefault(c => c.Id == userId);
        if (contact is not null)
            SelectedContact = contact;
    }

    /// <summary>
    /// Opens a conversation with someone who may not be in the roster yet — the members
    /// panel's "Message" action. The store prepends unknown users to its contact list,
    /// and the sync raised by that gives this view model the item to select.
    /// </summary>
    public async Task OpenConversationAsync(UserInfo user)
    {
        await _dms.OpenConversationAsync(user);

        var contact = Contacts.FirstOrDefault(c => c.Id == user.Id);
        if (contact is null)
            return;

        // The store has already loaded the thread; selecting through the property would
        // reload it, so the selection is applied with the handler suppressed.
        _suppressSelection = true;
        SelectedContact = contact;
        _suppressSelection = false;

        contact.UnreadCount = 0;
        SelectionChanged?.Invoke();
    }

    /// <summary>Uploaded files waiting in the DM composer.</summary>
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
            Error = $"Upload failed: {ex.Message}";
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

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        var attachments = PendingAttachments.Select(p => p.Attachment).ToArray();

        if ((text.Length == 0 && attachments.Length == 0) || SelectedContact is null)
            return;

        Draft = string.Empty;
        PendingAttachments.Clear();

        await _dms.SendMessageAsync(text, attachments.Length > 0 ? attachments : null);
    }

    [RelayCommand]
    private void ToggleMute(ContactItemViewModel contact)
    {
        _dms.ToggleContactNotificationsMuted(contact.Id);
        contact.IsMuted = _dms.IsContactNotificationsMuted(contact.Id);
    }

    /// <summary>
    /// Counts a message as unread when its conversation is not the open one. Runs off
    /// the store's own filtering, which only tracks the selected conversation.
    /// </summary>
    private void HandlePrivateMessage(PrivateMessage message) => OnUiThread(() =>
    {
        if (_disposed || message.SenderId == _auth.CurrentUser?.Id)
            return;

        if (SelectedContact?.Id == message.SenderId)
            return;

        var contact = Contacts.FirstOrDefault(c => c.Id == message.SenderId);

        if (contact is null)
        {
            // First message from someone new: the roster is stale, reload it. The
            // message itself is safe — the server keeps it; only the badge is at stake.
            _ = _dms.LoadContactsAsync();
            return;
        }

        contact.UnreadCount++;
    });

    private void HandleOnlineStatus(Guid userId, bool isOnline) => OnUiThread(() =>
    {
        var contact = Contacts.FirstOrDefault(c => c.Id == userId);
        if (contact is not null)
            contact.IsOnline = isOnline;
    });

    private void HandleStoreChanged() => OnUiThread(SyncFromStore);

    private void SyncFromStore()
    {
        if (_disposed)
            return;

        Error = _dms.Error;

        // Reconcile rather than rebuild, so per-item state the store does not know
        // about — unread counts — survives a contacts refresh.
        var known = Contacts.ToDictionary(c => c.Id);
        var seen = new HashSet<Guid>();

        foreach (var user in _dms.Contacts)
        {
            seen.Add(user.Id);

            if (known.TryGetValue(user.Id, out var existing))
            {
                existing.Name = string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName;
                existing.IsOnline = user.IsOnline;
                existing.IsMuted = _dms.IsContactNotificationsMuted(user.Id);
            }
            else
            {
                Contacts.Add(new ContactItemViewModel(user)
                {
                    IsMuted = _dms.IsContactNotificationsMuted(user.Id),
                    MuteCommand = ToggleMuteCommand
                });
            }
        }

        for (var i = Contacts.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Contacts[i].Id))
                Contacts.RemoveAt(i);
        }

        // The message list belongs to whichever conversation the store has loaded.
        if (SelectedContact is { } selected && _dms.SelectedContactId == selected.Id)
        {
            var myId = _auth.CurrentUser?.Id;

            Messages.Clear();
            var liveIds = new HashSet<Guid>();

            foreach (var message in _dms.Messages)
            {
                liveIds.Add(message.Id);

                // Cached by id so attachment bitmaps survive resyncs instead of being
                // re-downloaded on every new message.
                if (!_messageCache.TryGetValue(message.Id, out var item))
                {
                    item = new DmMessageItem(
                        message,
                        message.SenderId == myId,
                        message.Attachments.Select(a => new AttachmentItemViewModel(a, _api)).ToArray());

                    _messageCache[message.Id] = item;
                }

                Messages.Add(item);
            }

            foreach (var id in _messageCache.Keys.Where(id => !liveIds.Contains(id)).ToArray())
                _messageCache.Remove(id);
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

        _dms.OnChange -= HandleStoreChanged;
        _chat.OnPrivateMessageReceived -= HandlePrivateMessage;
        _chat.OnUserOnlineStatusChanged -= HandleOnlineStatus;

        GC.SuppressFinalize(this);
    }
}

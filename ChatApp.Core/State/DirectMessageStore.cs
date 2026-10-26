using ChatApp.Core.Services;
using ChatApp.Core.Abstractions;
using Shared.Models;
using System.Text.Json;

namespace ChatApp.Core.State;

/// <summary>
/// Manages direct message state: contacts, selected contact, and messages.
/// </summary>
public class DirectMessageStore : IDisposable
{
    private const string MutedDirectNotificationsPrefPrefix = "notifications.muted.direct.";
    private readonly ApiClientService _api;
    private readonly ChatClientService _chat;
    private readonly AuthStateProvider _auth;
    private readonly IAppPreferences _preferences;
    private readonly ChatRoomStore _rooms;
    private HashSet<Guid> _mutedDirectNotifications = [];
    private Guid? _notificationPrefsUserId;

    public UserInfo[] Contacts { get; private set; } = [];
    public Guid? SelectedContactId { get; private set; }
    public UserInfo? SelectedContact { get; private set; }
    public List<PrivateMessage> Messages { get; private set; } = [];
    public string? Error { get; private set; }

    public event Action? OnChange;

    public DirectMessageStore(
        ApiClientService api,
        ChatClientService chat,
        AuthStateProvider auth,
        IAppPreferences preferences,
        ChatRoomStore rooms)
    {
        _api = api;
        _chat = chat;
        _auth = auth;
        _preferences = preferences;
        _rooms = rooms;

        // Presence rides on the room store's server-member list: the contacts endpoint
        // never reports IsOnline (its projection omits it), so a fresh roster would
        // otherwise arrive all-offline and stay that way until someone toggles.
        _rooms.OnChange += HandleRoomStoreChanged;

        _chat.OnPrivateMessageReceived += HandlePrivateMessageReceived;
        _chat.OnPrivateMessagesRead += HandlePrivateMessagesRead;
        _chat.OnUserProfileUpdated += HandleUserProfileUpdated;
        _chat.OnUserOnlineStatusChanged += HandleUserOnlineStatusChanged;
    }

    /// <summary>
    /// Keeps contact presence live. Without this the store's snapshot kept its
    /// fetch-time IsOnline forever, and every roster reload stamped that stale value
    /// back over whatever the UI had learned from presence events.
    /// </summary>
    private void HandleUserOnlineStatusChanged(Guid userId, bool isOnline)
    {
        var changed = false;

        var contact = Contacts.FirstOrDefault(c => c.Id == userId);
        if (contact != null && contact.IsOnline != isOnline)
        {
            contact.IsOnline = isOnline;
            changed = true;
        }

        if (SelectedContact?.Id == userId && SelectedContact.IsOnline != isOnline)
        {
            SelectedContact.IsOnline = isOnline;
            changed = true;
        }

        if (changed) NotifyChanged();
    }

    public bool IsContactNotificationsMuted(Guid userId)
    {
        if (userId == Guid.Empty) return false;
        EnsureNotificationMuteStateLoaded();
        return _mutedDirectNotifications.Contains(userId);
    }

    public bool IsSelectedContactNotificationsMuted
        => SelectedContactId != null && IsContactNotificationsMuted(SelectedContactId.Value);

    public void SetContactNotificationsMuted(Guid userId, bool muted)
    {
        if (userId == Guid.Empty) return;
        EnsureNotificationMuteStateLoaded();

        var changed = muted
            ? _mutedDirectNotifications.Add(userId)
            : _mutedDirectNotifications.Remove(userId);

        if (!changed) return;
        SaveMutedDirectNotifications();
        NotifyChanged();
    }

    public void ToggleContactNotificationsMuted(Guid userId)
    {
        if (userId == Guid.Empty) return;
        SetContactNotificationsMuted(userId, !IsContactNotificationsMuted(userId));
    }

    public async Task LoadContactsAsync()
    {
        try
        {
            Error = null;
            var contacts = await _api.GetContactsAsync();
            OverlayPresence(contacts);
            Contacts = contacts;
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Error = $"Failed to load contacts: {ex.Message}";
            NotifyChanged();
        }
    }

    /// <summary>
    /// Stamps live presence onto contacts. GetContactsAsync's server projection never
    /// sets IsOnline; the server-member list is the presence authority.
    /// </summary>
    private bool OverlayPresence(IEnumerable<UserInfo> contacts)
    {
        var changed = false;

        foreach (var contact in contacts)
        {
            var server = _rooms.ServerMembers.FirstOrDefault(m => m.UserId == contact.Id);
            if (server != null && contact.IsOnline != server.IsOnline)
            {
                contact.IsOnline = server.IsOnline;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Re-overlays when the server-member list refreshes, which covers contacts loaded
    /// before the first presence-bearing fetch existed.
    /// </summary>
    private void HandleRoomStoreChanged()
    {
        var changed = OverlayPresence(Contacts);

        if (SelectedContact != null && OverlayPresence([SelectedContact]))
            changed = true;

        if (changed) NotifyChanged();
    }

    public async Task SelectContactAsync(UserInfo contact)
    {
        SelectedContactId = contact.Id;
        SelectedContact = contact;
        Messages.Clear();
        Error = null;
        NotifyChanged();

        try
        {
            await _chat.MarkPrivateMessageReadAsync(contact.Id);
            var msgs = await _api.GetPrivateMessagesAsync(contact.Id);
            foreach (var message in msgs)
            {
                _api.NormalizeAttachments(message.Attachments);
            }
            Messages = msgs.Reverse().ToList();
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Error = $"Failed to load messages: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task OpenConversationAsync(UserInfo contact)
    {
        var existing = Contacts.FirstOrDefault(c => c.Id == contact.Id);
        if (existing == null)
        {
            Contacts = [contact, ..Contacts];
            existing = contact;
            NotifyChanged();
        }

        await SelectContactAsync(existing);
    }

    public async Task SendMessageAsync(string content, AttachmentInfo[]? attachments = null)
    {
        if (SelectedContactId == null) return;
        var hasText = !string.IsNullOrWhiteSpace(content);
        var hasAttachments = attachments is { Length: > 0 };
        if (!hasText && !hasAttachments) return;

        try
        {
            await _chat.SendPrivateMessageAsync(SelectedContactId.Value, content.Trim(), attachments: attachments);
        }
        catch (Exception ex)
        {
            Error = $"Failed to send message: {ex.Message}";
            NotifyChanged();
        }
    }

    private void HandlePrivateMessageReceived(PrivateMessage message)
    {
        var myId = _auth.CurrentUser?.Id;
        if (myId == null || SelectedContactId == null) return;

        // Only show messages in the conversation between current user and selected contact
        var isFromContact = message.SenderId == SelectedContactId && message.RecipientId == myId;
        var isToContact = message.RecipientId == SelectedContactId && message.SenderId == myId;
        if (isFromContact || isToContact)
        {
            _api.NormalizeAttachments(message.Attachments);
            Messages.Add(message);
            NotifyChanged();
        }
    }

    private void HandlePrivateMessagesRead(Guid userId)
    {
        // Refresh read status if relevant
        NotifyChanged();
    }

    private void HandleUserProfileUpdated(UserProfileUpdateInfo profile)
    {
        var changed = false;

        var contact = Contacts.FirstOrDefault(c => c.Id == profile.UserId);
        if (contact != null)
        {
            contact.DisplayName = profile.DisplayName;
            contact.AvatarUrl = profile.AvatarUrl;
            changed = true;
        }

        if (SelectedContact?.Id == profile.UserId)
        {
            SelectedContact.DisplayName = profile.DisplayName;
            SelectedContact.AvatarUrl = profile.AvatarUrl;
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

    private void NotifyChanged() => OnChange?.Invoke();

    private void EnsureNotificationMuteStateLoaded()
    {
        var userId = _auth.CurrentUser?.Id;
        if (_notificationPrefsUserId == userId)
        {
            return;
        }

        _notificationPrefsUserId = userId;
        _mutedDirectNotifications = LoadMutedDirectNotifications();
    }

    private string GetMutedDirectNotificationsPrefKey()
    {
        var userToken = _auth.CurrentUser?.Id.ToString("D") ?? "anonymous";
        return $"{MutedDirectNotificationsPrefPrefix}{userToken}";
    }

    private HashSet<Guid> LoadMutedDirectNotifications()
    {
        try
        {
            var raw = _preferences.Get(GetMutedDirectNotificationsPrefKey(), string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            var ids = JsonSerializer.Deserialize<Guid[]>(raw) ?? [];
            return ids
                .Where(id => id != Guid.Empty)
                .ToHashSet();
        }
        catch
        {
            return [];
        }
    }

    private void SaveMutedDirectNotifications()
    {
        try
        {
            var payload = JsonSerializer.Serialize(_mutedDirectNotifications.ToArray());
            _preferences.Set(GetMutedDirectNotificationsPrefKey(), payload);
        }
        catch
        {
            // Ignore preference persistence failures.
        }
    }

    public void Dispose()
    {
        _chat.OnPrivateMessageReceived -= HandlePrivateMessageReceived;
        _chat.OnPrivateMessagesRead -= HandlePrivateMessagesRead;
        _chat.OnUserProfileUpdated -= HandleUserProfileUpdated;
        _chat.OnUserOnlineStatusChanged -= HandleUserOnlineStatusChanged;
        _rooms.OnChange -= HandleRoomStoreChanged;
    }
}

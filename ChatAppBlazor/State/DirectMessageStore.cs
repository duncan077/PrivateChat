using ChatAppBlazor.Services;
using Microsoft.Maui.Storage;
using Shared.Models;
using System.Text.Json;

namespace ChatAppBlazor.State;

/// <summary>
/// Manages direct message state: contacts, selected contact, and messages.
/// </summary>
public class DirectMessageStore : IDisposable
{
    private const string MutedDirectNotificationsPrefPrefix = "notifications.muted.direct.";
    private readonly ApiClientService _api;
    private readonly ChatClientService _chat;
    private readonly AuthStateProvider _auth;
    private HashSet<Guid> _mutedDirectNotifications = [];
    private Guid? _notificationPrefsUserId;

    public UserInfo[] Contacts { get; private set; } = [];
    public Guid? SelectedContactId { get; private set; }
    public UserInfo? SelectedContact { get; private set; }
    public List<PrivateMessage> Messages { get; private set; } = [];
    public string? Error { get; private set; }

    public event Action? OnChange;

    public DirectMessageStore(ApiClientService api, ChatClientService chat, AuthStateProvider auth)
    {
        _api = api;
        _chat = chat;
        _auth = auth;

        _chat.OnPrivateMessageReceived += HandlePrivateMessageReceived;
        _chat.OnPrivateMessagesRead += HandlePrivateMessagesRead;
        _chat.OnUserProfileUpdated += HandleUserProfileUpdated;
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
            Contacts = await _api.GetContactsAsync();
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Error = $"Failed to load contacts: {ex.Message}";
            NotifyChanged();
        }
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
            var raw = Preferences.Default.Get(GetMutedDirectNotificationsPrefKey(), string.Empty);
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
            Preferences.Default.Set(GetMutedDirectNotificationsPrefKey(), payload);
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
    }
}

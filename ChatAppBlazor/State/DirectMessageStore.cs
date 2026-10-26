using ChatAppBlazor.Serialization;
using ChatAppBlazor.Services;
using Microsoft.Maui.Storage;
using Shared;
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
    /// <summary>Per-conversation unread counts, keyed by peer user id.</summary>
    public Dictionary<Guid, int> UnreadCounts { get; private set; } = [];
    private readonly TypingTracker _typing = new();
    private readonly CancellationTokenSource _typingSweepCts = new();
    private readonly Task _typingSweepLoopTask;
    private DateTime _lastTypingSentAt = DateTime.MinValue;

    public UserInfo[] Contacts { get; private set; } = [];
    public Guid? SelectedContactId { get; private set; }
    public UserInfo? SelectedContact { get; private set; }
    public List<PrivateMessage> Messages { get; } = [];
    public string? Error { get; private set; }

    /// <summary>
    /// Bumped whenever a message is mutated in place (edit, delete). See the same
    /// member on ChatRoomStore — the render loop's scroll gate keys off it.
    /// </summary>
    public int MessagesVersion { get; private set; }

    /// <summary>False once a history fetch comes back short, i.e. we have reached the start.</summary>
    public bool HasMoreHistory { get; private set; } = true;
    public bool IsLoadingOlder { get; private set; }

    private readonly HashSet<Guid> _messageIds = [];
    private int _selectionVersion;
    private int _reconnectVersion;
    private MessageCursor? _disconnectedCursor;
    private bool _needsRecovery;
    private static MessageCursor Position(PrivateMessage message) => new() { SentAt = message.SentAt, Id = message.Id };
    private void MergeMessages(IEnumerable<PrivateMessage> messages)
    {
        foreach (var message in messages) _api.NormalizeAttachments(message.Attachments);
        MessageHistory.Merge(Messages, messages, Position, _messageIds);
    }

    public event Action? OnChange;

    public DirectMessageStore(ApiClientService api, ChatClientService chat, AuthStateProvider auth)
    {
        _api = api;
        _chat = chat;
        _auth = auth;

        _chat.OnPrivateMessageReceived += HandlePrivateMessageReceived;
        _chat.OnPrivateMessagesRead += HandlePrivateMessagesRead;
        _chat.OnPrivateMessageDeleted += HandlePrivateMessageDeleted;
        _chat.OnPrivateMessageEdited += HandlePrivateMessageEdited;
        _chat.OnPrivateMessageReactionChanged += HandlePrivateMessageReactionChanged;
        _chat.OnPrivateTyping += HandlePrivateTyping;
        _chat.OnPrivateConversationRead += HandlePrivateConversationRead;
        // This store never resynced after a reconnect, so DM state stayed whatever it
        // was when the socket dropped.
        _chat.OnConnectionStateChanged += HandleConnectionStateChanged;

        _typingSweepLoopTask = RunTypingSweepLoopAsync(_typingSweepCts.Token);
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
            await RefreshUnreadCountsAsync();
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
        var selection = ++_selectionVersion;
        _disconnectedCursor = null;
        _needsRecovery = false;
        IsLoadingOlder = false;
        HasMoreHistory = true;
        SelectedContactId = contact.Id;
        SelectedContact = contact;
        _typing.Clear();
        Messages.Clear();
        _messageIds.Clear();
        // Opening a conversation reads it.
        UnreadCounts.Remove(contact.Id);
        Error = null;
        NotifyChanged();

        try
        {
            await _chat.MarkPrivateMessageReadAsync(contact.Id);
            if (selection != _selectionVersion) return;
            var msgs = await _api.GetPrivateMessagePageAsync(contact.Id);
            if (selection != _selectionVersion) return;
            MergeMessages(msgs);
            // A short first page means the whole conversation fits in one.
            HasMoreHistory = msgs.Length >= 50;
            NotifyChanged();
        }
        catch (Exception ex)
        {
            if (selection != _selectionVersion) return;
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

    public async Task<bool> SendMessageAsync(string content, AttachmentInfo[]? attachments = null, Guid? replyToMessageId = null)
    {
        if (SelectedContactId == null) return false;
        var hasText = !string.IsNullOrWhiteSpace(content);
        var hasAttachments = attachments is { Length: > 0 };
        if (!hasText && !hasAttachments) return false;

        try
        {
            await _chat.SendPrivateMessageAsync(SelectedContactId.Value, content.Trim(), attachments: attachments, replyToMessageId: replyToMessageId);
            return true;
        }
        catch (Exception ex)
        {
            Error = $"Failed to send message: {ex.Message}";
            NotifyChanged();
            return false;
        }
    }

    private void HandlePrivateMessageReceived(PrivateMessage message)
    {
        var myId = _auth.CurrentUser?.Id;
        if (myId == null) return;

        // Only show messages in the conversation between current user and selected contact
        var isFromContact = message.SenderId == SelectedContactId && message.RecipientId == myId;
        var isToContact = message.RecipientId == SelectedContactId && message.SenderId == myId;
        if (isFromContact || isToContact)
        {
            // The message is the proof they stopped typing; don't wait for expiry.
            _typing.Remove(message.SenderId);
            _api.NormalizeAttachments(message.Attachments);
            MergeMessages([message]);

            // Arriving in the open conversation counts as read straight away.
            if (isFromContact)
            {
                _ = MarkSelectedConversationReadAsync();
            }

            NotifyChanged();
            return;
        }

        // A conversation that is not on screen: badge it, but never for our own echo.
        if (message.SenderId == myId) return;

        UnreadCounts[message.SenderId] = GetUnreadCount(message.SenderId) + 1;
        NotifyChanged();
    }

    private void HandlePrivateMessagesRead(Guid userId)
    {
        // Refresh read status if relevant
        NotifyChanged();
    }

    /// <summary>
    /// True when a DM delta belongs to the conversation currently on screen. Both
    /// participant ids are on the event, so this needs no lookup.
    /// </summary>
    private bool IsSelectedConversation(Guid senderId, Guid recipientId)
    {
        var myId = _auth.CurrentUser?.Id;
        if (myId == null || SelectedContactId == null) return false;

        return (senderId == SelectedContactId && recipientId == myId) ||
               (recipientId == SelectedContactId && senderId == myId);
    }

    private void HandlePrivateMessageDeleted(Guid senderId, Guid recipientId, Guid messageId)
    {
        if (!IsSelectedConversation(senderId, recipientId)) return;

        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg != null) msg.IsDeleted = true;

        // Loaded replies quoting this message must stop showing stale content.
        foreach (var reply in Messages.Where(m => m.ReplyToMessageId == messageId && m.ReplyPreview != null))
        {
            reply.ReplyPreview!.IsUnavailable = true;
        }

        MessagesVersion++;
        NotifyChanged();
    }

    /// <summary>Prepends the previous page of this conversation. See ChatRoomStore for the de-dup rationale.</summary>
    public async Task<bool> LoadOlderMessagesAsync(int count = 50)
    {
        if (SelectedContactId is not { } conversationId || Messages.Count == 0 || IsLoadingOlder || !HasMoreHistory) return false;
        count = Math.Clamp(count, 1, 100);
        var selection = _selectionVersion;
        var oldest = Position(Messages[0]);
        IsLoadingOlder = true;
        NotifyChanged();
        try
        {
            var older = await _api.GetPrivateMessagePageAsync(conversationId, count, before: oldest);
            if (selection != _selectionVersion || SelectedContactId != conversationId) return false;
            HasMoreHistory = older.Length >= count;
            var previousCount = Messages.Count;
            MergeMessages(older);
            return Messages.Count != previousCount;
        }
        catch (Exception ex)
        {
            if (selection == _selectionVersion && SelectedContactId == conversationId) Error = $"Failed to load older messages: {ex.Message}";
            return false;
        }
        finally
        {
            if (selection == _selectionVersion && SelectedContactId == conversationId)
            {
                IsLoadingOlder = false;
                NotifyChanged();
            }
        }
    }

    private void HandlePrivateMessageEdited(Guid senderId, Guid recipientId, Guid messageId, string content, DateTime editedAt)
    {
        if (!IsSelectedConversation(senderId, recipientId)) return;

        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg != null)
        {
            msg.Content = content;
            msg.EditedAt = editedAt;
        }

        MessagesVersion++;
        NotifyChanged();
    }

    private void HandlePrivateMessageReactionChanged(Guid senderId, Guid recipientId, Guid messageId, Guid userId, string emoji, bool added)
    {
        if (!IsSelectedConversation(senderId, recipientId)) return;

        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg == null) return;

        msg.Reactions = ReactionState.Apply(msg.Reactions, userId, emoji, added);
        MessagesVersion++;
        NotifyChanged();
    }

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
        if (SelectedContactId is not { } conversationId)
        {
            await LoadContactsAsync();
            return;
        }
        var selection = _selectionVersion;
        var after = _needsRecovery ? _disconnectedCursor : (Messages.Count == 0 ? null : Position(Messages[^1]));
        bool IsCurrent() => selection == _selectionVersion && reconnect == _reconnectVersion && SelectedContactId == conversationId;
        try
        {
            if (!IsCurrent()) return;
            await MessageHistory.RecoverAsync(after,
                cursor => _api.GetPrivateMessagePageAsync(conversationId, after: cursor),
                Position, IsCurrent, page =>
                {
                    MergeMessages(page);
                    NotifyChanged();
                });
            if (!IsCurrent()) return;
            _disconnectedCursor = null;
            _needsRecovery = false;
            Error = null;
            await LoadContactsAsync();
            if (IsCurrent()) await MarkSelectedConversationReadAsync();
            if (IsCurrent()) NotifyChanged();
        }
        catch (Exception ex)
        {
            if (!IsCurrent()) return;
            Error = $"Failed to recover messages: {ex.Message}";
            NotifyChanged();
        }
    }

    /// <summary>Replaces local DM badge state with the server's view.</summary>
    public async Task RefreshUnreadCountsAsync()
    {
        try
        {
            var counts = await _api.GetPrivateUnreadCountsAsync();
            UnreadCounts = counts.ToDictionary(c => c.PeerUserId, c => c.UnreadCount);

            // The open conversation is read by definition.
            if (SelectedContactId is { } openContactId)
            {
                UnreadCounts.Remove(openContactId);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirectMessageStore] Unread refresh failed: {ex.Message}");
        }
    }

    public int GetUnreadCount(Guid peerUserId)
        => UnreadCounts.TryGetValue(peerUserId, out var count) ? count : 0;

    private async Task MarkSelectedConversationReadAsync()
    {
        if (SelectedContactId is not { } contactId) return;

        try
        {
            await _chat.MarkPrivateMessageReadAsync(contactId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirectMessageStore] Mark-read failed: {ex.Message}");
        }
    }

    private void HandlePrivateConversationRead(Guid readerUserId, Guid peerUserId)
    {
        var myId = _auth.CurrentUser?.Id;
        if (myId == null) return;

        if (readerUserId == myId)
        {
            // Another of this user's devices opened the conversation.
            if (UnreadCounts.Remove(peerUserId))
            {
                NotifyChanged();
            }
            return;
        }

        // The peer read our messages: show it on the ones we sent them.
        if (readerUserId == SelectedContactId && peerUserId == myId)
        {
            var changed = false;
            foreach (var message in Messages.Where(m => m.SenderId == myId && !m.IsRead))
            {
                message.IsRead = true;
                changed = true;
            }

            if (changed)
            {
                MessagesVersion++;
                NotifyChanged();
            }
        }
    }

    private void HandlePrivateTyping(Guid userId, string userName)
    {
        // Only show typing for the conversation actually on screen.
        if (userId != SelectedContactId) return;

        if (_typing.Touch(userId, userName, DateTime.UtcNow))
        {
            NotifyChanged();
        }
    }

    /// <summary>Null when the selected contact is not typing.</summary>
    public string? TypingSummary => _typing.Describe(DateTime.UtcNow);

    public async Task NotifyTypingAsync()
    {
        if (SelectedContactId == null) return;

        var now = DateTime.UtcNow;
        if (now - _lastTypingSentAt < TimeSpan.FromSeconds(ChatLimits.TypingThrottleSeconds)) return;
        _lastTypingSentAt = now;

        try
        {
            await _chat.SetPrivateTypingAsync(SelectedContactId.Value);
        }
        catch
        {
            // A dropped typing signal is not worth surfacing.
        }
    }

    /// <summary>Ages out typing indicators — the server sends no stop event.</summary>
    private async Task RunTypingSweepLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
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

    public async Task ToggleReactionAsync(Guid messageId, string emoji)
    {
        try
        {
            await _chat.TogglePrivateReactionAsync(messageId, emoji);
        }
        catch (Exception ex)
        {
            Error = $"Failed to react: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task DeleteMessageAsync(Guid messageId)
    {
        try
        {
            await _chat.DeletePrivateMessageAsync(messageId);
        }
        catch (Exception ex)
        {
            Error = $"Failed to delete message: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task<bool> EditMessageAsync(Guid messageId, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        try
        {
            await _chat.EditPrivateMessageAsync(messageId, content.Trim());
            return true;
        }
        catch (Exception ex)
        {
            Error = $"Failed to edit message: {ex.Message}";
            NotifyChanged();
            return false;
        }
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

            var ids = JsonSerializer.Deserialize(raw, AppJsonContext.Default.GuidArray) ?? [];
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
            var payload = JsonSerializer.Serialize(_mutedDirectNotifications.ToArray(), AppJsonContext.Default.GuidArray);
            Preferences.Default.Set(GetMutedDirectNotificationsPrefKey(), payload);
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
        _chat.OnPrivateMessageReceived -= HandlePrivateMessageReceived;
        _chat.OnPrivateMessagesRead -= HandlePrivateMessagesRead;
        _chat.OnPrivateMessageDeleted -= HandlePrivateMessageDeleted;
        _chat.OnPrivateMessageEdited -= HandlePrivateMessageEdited;
        _chat.OnPrivateMessageReactionChanged -= HandlePrivateMessageReactionChanged;
        _chat.OnPrivateTyping -= HandlePrivateTyping;
        _chat.OnPrivateConversationRead -= HandlePrivateConversationRead;
        _chat.OnConnectionStateChanged -= HandleConnectionStateChanged;
        _chat.OnUserProfileUpdated -= HandleUserProfileUpdated;

        _typingSweepCts.Cancel();
        _typingSweepCts.Dispose();
    }
}

using ChatAppBlazor.Services;
using Shared.Models;

namespace ChatAppBlazor.State;

/// <summary>
/// Manages direct message state: contacts, selected contact, and messages.
/// </summary>
public class DirectMessageStore : IDisposable
{
    private readonly ApiClientService _api;
    private readonly ChatClientService _chat;
    private readonly AuthStateProvider _auth;

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
            Messages = msgs.Reverse().ToList();
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Error = $"Failed to load messages: {ex.Message}";
            NotifyChanged();
        }
    }

    public async Task SendMessageAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || SelectedContactId == null) return;
        try
        {
            await _chat.SendPrivateMessageAsync(SelectedContactId.Value, content);
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
            Messages.Add(message);
            NotifyChanged();
        }
    }

    private void HandlePrivateMessagesRead(Guid userId)
    {
        // Refresh read status if relevant
        NotifyChanged();
    }

    private void NotifyChanged() => OnChange?.Invoke();

    public void Dispose()
    {
        _chat.OnPrivateMessageReceived -= HandlePrivateMessageReceived;
        _chat.OnPrivateMessagesRead -= HandlePrivateMessagesRead;
    }
}

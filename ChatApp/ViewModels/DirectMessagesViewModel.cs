using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ChatApp.Services;
using Shared.Models;

namespace ChatApp.ViewModels;

public partial class DirectMessagesViewModel : ViewModelBase
{
    private readonly ApiClientService _api;
    private readonly AuthStateProvider _auth;
    private readonly ChatClientService _chat;

    public ObservableCollection<UserInfo> Contacts { get; } = [];
    public ObservableCollection<PrivateMessage> Messages { get; } = [];

    [ObservableProperty] private UserInfo? _selectedContact;
    [ObservableProperty] private string _messageText = "";
    [ObservableProperty] private bool _hasMoreMessages;

    private const int PageSize = 50;

    public DirectMessagesViewModel(ApiClientService api, AuthStateProvider auth, ChatClientService chat)
    {
        _api = api;
        _auth = auth;
        _chat = chat;

        _chat.OnPrivateMessageReceived += OnPrivateMessageReceived;
        _chat.OnPrivateMessagesRead += OnPrivateMessagesRead;
    }

    public async Task LoadContactsAsync()
    {
        try
        {
            var contacts = await _api.GetContactsAsync();
            Contacts.Clear();
            foreach (var c in contacts) Contacts.Add(c);
        }
        catch { }
    }

    async partial void OnSelectedContactChanged(UserInfo? value)
    {
        Messages.Clear();
        HasMoreMessages = false;
        if (value == null) return;

        try
        {
            var msgs = await _api.GetPrivateMessagesAsync(value.Id, PageSize);
            HasMoreMessages = msgs.Length >= PageSize;
            foreach (var msg in msgs.Reverse()) Messages.Add(msg);
            await _chat.MarkPrivateMessageReadAsync(value.Id);
        }
        catch { }
    }

    [RelayCommand]
    private async Task LoadOlderMessagesAsync()
    {
        if (SelectedContact == null || !HasMoreMessages || Messages.Count == 0) return;
        try
        {
            var oldest = Messages[0].SentAt;
            var older = await _api.GetPrivateMessagesAsync(SelectedContact.Id, PageSize, oldest);
            HasMoreMessages = older.Length >= PageSize;
            for (var i = older.Length - 1; i >= 0; i--)
                Messages.Insert(0, older[i]);
        }
        catch { }
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(MessageText) || SelectedContact == null) return;
        var text = MessageText;
        MessageText = "";
        try
        {
            await _chat.SendPrivateMessageAsync(SelectedContact.Id, text);
        }
        catch { }
    }

    public async Task SelectContactAsync(UserInfo user)
    {
        await LoadContactsAsync();
        var contact = Contacts.FirstOrDefault(c => c.Id == user.Id);
        if (contact != null)
        {
            SelectedContact = contact;
            return;
        }

        Contacts.Insert(0, user);
        SelectedContact = user;
    }

    private void OnPrivateMessageReceived(PrivateMessage message)
    {
        if (SelectedContact == null) return;

        if (message.SenderId == SelectedContact.Id || message.RecipientId == SelectedContact.Id)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Messages.Add(message));
        }
    }

    private void OnPrivateMessagesRead(Guid userId)
    {
        if (SelectedContact?.Id == userId)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var msg in Messages)
                    msg.IsRead = true;
            });
        }
    }
}

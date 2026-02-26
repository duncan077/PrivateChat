using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ChatApp.Services;
using Shared.Models;

namespace ChatApp.ViewModels;

public partial class ChatRoomViewModel : ViewModelBase
{
    private readonly ApiClientService _api;
    private readonly AuthStateProvider _auth;
    private readonly ChatClientService _chat;
    private readonly NavigationService _nav;

    public ObservableCollection<ChatRoomInfo> Rooms { get; } = [];
    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<ChatMemberInfo> Members { get; } = [];

    [ObservableProperty] private ChatRoomInfo? _selectedRoom;
    [ObservableProperty] private string _messageText = "";
    [ObservableProperty] private bool _showCreateRoom;
    [ObservableProperty] private bool _showMembers;
    [ObservableProperty] private string _newRoomName = "";
    [ObservableProperty] private string? _newRoomDesc;
    [ObservableProperty] private bool _newRoomVoice;
    [ObservableProperty] private bool _newRoomVideo;
    [ObservableProperty] private UserRole _currentUserRole = UserRole.Member;
    [ObservableProperty] private bool _hasMoreMessages;

    private const int PageSize = 50;

    public ChatRoomViewModel(ApiClientService api, AuthStateProvider auth, ChatClientService chat, NavigationService nav)
    {
        _api = api;
        _auth = auth;
        _chat = chat;
        _nav = nav;

        _chat.OnMessageReceived += OnMessageReceived;
        _chat.OnMessageDeleted += OnMessageDeleted;
        _chat.OnRoomDeleted += OnRoomDeleted;
        _chat.OnUserJoinedRoom += OnUserJoined;
        _chat.OnUserLeftRoom += OnUserLeft;
    }

    public async Task LoadRoomsAsync()
    {
        try
        {
            var rooms = await _api.GetRoomsAsync();
            Rooms.Clear();
            foreach (var room in rooms) Rooms.Add(room);
        }
        catch { }
    }

    async partial void OnSelectedRoomChanged(ChatRoomInfo? oldValue, ChatRoomInfo? newValue)
    {
        if (oldValue != null)
        {
            try { await _chat.LeaveRoomAsync(oldValue.Id); } catch { }
        }

        Messages.Clear();
        Members.Clear();
        HasMoreMessages = false;
        OnPropertyChanged(nameof(IsRoomOwner));

        if (newValue == null) return;

        try
        {
            await _chat.JoinRoomAsync(newValue.Id);
            var msgs = await _api.GetRoomMessagesAsync(newValue.Id, PageSize);
            HasMoreMessages = msgs.Length >= PageSize;
            foreach (var msg in msgs.Reverse()) Messages.Add(msg);
            var members = await _api.GetRoomMembersAsync(newValue.Id);
            foreach (var m in members) Members.Add(m);
            CurrentUserRole = members.FirstOrDefault(m => m.UserId == _auth.CurrentUser?.Id)?.Role ?? UserRole.Member;
        }
        catch { }
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(MessageText) || SelectedRoom == null) return;
        var text = MessageText;
        MessageText = "";
        try
        {
            await _chat.SendMessageAsync(SelectedRoom.Id, text);
        }
        catch { }
    }

    [RelayCommand]
    private async Task DeleteMessageAsync(Guid messageId)
    {
        if (SelectedRoom == null) return;
        try
        {
            await _chat.DeleteMessageAsync(SelectedRoom.Id, messageId);
        }
        catch { }
    }

    [RelayCommand]
    private async Task KickUserAsync(Guid userId)
    {
        if (SelectedRoom == null) return;
        try
        {
            await _chat.KickUserAsync(SelectedRoom.Id, userId);
            var members = await _api.GetRoomMembersAsync(SelectedRoom.Id);
            Members.Clear();
            foreach (var m in members) Members.Add(m);
        }
        catch { }
    }

    [RelayCommand]
    private async Task CreateRoomAsync()
    {
        if (string.IsNullOrWhiteSpace(NewRoomName)) return;
        try
        {
            await _api.CreateRoomAsync(NewRoomName, NewRoomDesc, NewRoomVoice, NewRoomVideo);
            await LoadRoomsAsync();
            ShowCreateRoom = false;
            NewRoomName = "";
            NewRoomDesc = null;
            NewRoomVoice = false;
            NewRoomVideo = false;
        }
        catch { }
    }

    [RelayCommand]
    private async Task LoadOlderMessagesAsync()
    {
        if (SelectedRoom == null || !HasMoreMessages || Messages.Count == 0) return;
        try
        {
            var oldest = Messages[0].SentAt;
            var older = await _api.GetRoomMessagesAsync(SelectedRoom.Id, PageSize, oldest);
            HasMoreMessages = older.Length >= PageSize;
            for (var i = older.Length - 1; i >= 0; i--)
                Messages.Insert(0, older[i]);
        }
        catch { }
    }

    [RelayCommand]
    private async Task DeleteRoomAsync()
    {
        if (SelectedRoom == null) return;
        try
        {
            await _chat.DeleteRoomAsync(SelectedRoom.Id);
        }
        catch { }
    }

    public bool IsRoomOwner => SelectedRoom != null && _auth.CurrentUser != null && SelectedRoom.OwnerId == _auth.CurrentUser.Id;

    [RelayCommand]
    private void ToggleCreateRoom() => ShowCreateRoom = !ShowCreateRoom;

    [RelayCommand]
    private void ToggleMembers() => ShowMembers = !ShowMembers;

    [RelayCommand]
    private void StartPrivateMessage(ChatMemberInfo member)
    {
        if (member.UserId == _auth.CurrentUser?.Id) return;
        _nav.NavigateToPrivateMessage(new UserInfo
        {
            Id = member.UserId,
            UserName = member.UserName,
            DisplayName = member.DisplayName,
            AvatarUrl = member.AvatarUrl,
            IsOnline = member.IsOnline
        });
    }

    private void OnRoomDeleted(Guid roomId)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var room = Rooms.FirstOrDefault(r => r.Id == roomId);
            if (room != null)
            {
                if (SelectedRoom?.Id == roomId)
                    SelectedRoom = null;
                Rooms.Remove(room);
            }
        });
    }

    private void OnMessageReceived(ChatMessage message)
    {
        if (SelectedRoom != null && message.RoomId == SelectedRoom.Id)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Messages.Add(message));
        }
    }

    private void OnMessageDeleted(Guid roomId, Guid messageId)
    {
        if (SelectedRoom != null && roomId == SelectedRoom.Id)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var msg = Messages.FirstOrDefault(m => m.Id == messageId);
                if (msg != null) msg.IsDeleted = true;
            });
        }
    }

    private void OnUserJoined(Guid roomId, ChatMemberInfo member)
    {
        if (SelectedRoom != null && roomId == SelectedRoom.Id)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Members.Add(member));
        }
    }

    private void OnUserLeft(Guid roomId, Guid userId)
    {
        if (SelectedRoom != null && roomId == SelectedRoom.Id)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var member = Members.FirstOrDefault(m => m.UserId == userId);
                if (member != null) Members.Remove(member);
            });
        }
    }
}

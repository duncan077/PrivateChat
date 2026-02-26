using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ChatApp.Services;
using Microsoft.Extensions.DependencyInjection;
using Shared.Models;

namespace ChatApp.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly AuthStateProvider _auth;
    private readonly NavigationService _nav;

    [ObservableProperty] private bool _isAuthenticated;
    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _selectedTab = "rooms";

    public MainViewModel(AuthStateProvider auth, NavigationService nav)
    {
        _auth = auth;
        _nav = nav;

        _auth.OnAuthStateChanged += OnAuthStateChanged;
        _nav.OnNavigated += vm => CurrentPage = vm;
        _nav.OnNavigateToPrivateMessage += user =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => NavigateToMessagesWith(user));

        CurrentPage = App.Services.GetRequiredService<LoginViewModel>();
    }

    private void OnAuthStateChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsAuthenticated = _auth.IsAuthenticated;
            DisplayName = _auth.CurrentUser?.DisplayName;

            if (IsAuthenticated)
            {
                NavigateToRooms();
            }
            else
            {
                CurrentPage = App.Services.GetRequiredService<LoginViewModel>();
                SelectedTab = "rooms";
            }
        });
    }

    [RelayCommand]
    private void NavigateToRooms()
    {
        SelectedTab = "rooms";
        var vm = App.Services.GetRequiredService<ChatRoomViewModel>();
        CurrentPage = vm;
        _ = vm.LoadRoomsAsync();
    }

    [RelayCommand]
    private void NavigateToMessages()
    {
        SelectedTab = "messages";
        var vm = App.Services.GetRequiredService<DirectMessagesViewModel>();
        CurrentPage = vm;
        _ = vm.LoadContactsAsync();
    }

    public void NavigateToMessagesWith(UserInfo user)
    {
        SelectedTab = "messages";
        var vm = App.Services.GetRequiredService<DirectMessagesViewModel>();
        CurrentPage = vm;
        _ = vm.SelectContactAsync(user);
    }

    [RelayCommand]
    private void NavigateToVoice()
    {
        SelectedTab = "voice";
        var vm = App.Services.GetRequiredService<VoiceVideoViewModel>();
        CurrentPage = vm;
        if (vm.Rooms.Count == 0)
            _ = vm.LoadRoomsAsync();
    }

    [RelayCommand]
    private void Logout()
    {
        _auth.ClearAuth();
    }
}

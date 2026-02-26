using CommunityToolkit.Mvvm.ComponentModel;
using Shared.Models;

namespace ChatApp.Services;

public class NavigationService : ObservableObject
{
    private object? _currentView;

    public object? CurrentView
    {
        get => _currentView;
        set => SetProperty(ref _currentView, value);
    }

    public event Action<object?>? OnNavigated;
    public event Action<UserInfo>? OnNavigateToPrivateMessage;

    public void NavigateTo(object viewModel)
    {
        CurrentView = viewModel;
        OnNavigated?.Invoke(viewModel);
    }

    public void NavigateToPrivateMessage(UserInfo user)
    {
        OnNavigateToPrivateMessage?.Invoke(user);
    }
}

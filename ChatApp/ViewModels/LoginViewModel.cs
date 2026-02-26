using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ChatApp.Services;

namespace ChatApp.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly ApiClientService _api;
    private readonly AuthStateProvider _auth;
    private readonly ChatClientService _chat;
    private readonly NavigationService _nav;

    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isLoading;

    public LoginViewModel(ApiClientService api, AuthStateProvider auth, ChatClientService chat, NavigationService nav)
    {
        _api = api;
        _auth = auth;
        _chat = chat;
        _nav = nav;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        Error = null;

        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrWhiteSpace(Password))
        {
            Error = "Username and password are required";
            return;
        }

        IsLoading = true;

        try
        {
            var response = await _api.LoginAsync(UserName, Password);
            if (response.Success)
            {
                _auth.SetAuth(response.Token!, response.User!);
                await _chat.ConnectAsync();
            }
            else
            {
                Error = response.Error;
            }
        }
        catch (Exception ex)
        {
            Error = $"Connection error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void GoToRegister()
    {
        _nav.NavigateTo(App.Services.GetService(typeof(RegisterViewModel))!);
    }
}

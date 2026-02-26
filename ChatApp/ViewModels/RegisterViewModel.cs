using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ChatApp.Services;

namespace ChatApp.ViewModels;

public partial class RegisterViewModel : ViewModelBase
{
    private readonly ApiClientService _api;
    private readonly AuthStateProvider _auth;
    private readonly ChatClientService _chat;
    private readonly NavigationService _nav;

    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isLoading;

    public RegisterViewModel(ApiClientService api, AuthStateProvider auth, ChatClientService chat, NavigationService nav)
    {
        _api = api;
        _auth = auth;
        _chat = chat;
        _nav = nav;
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        Error = null;

        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrWhiteSpace(Password))
        {
            Error = "Username and password are required";
            return;
        }

        if (Password != ConfirmPassword)
        {
            Error = "Passwords do not match";
            return;
        }

        IsLoading = true;

        try
        {
            var response = await _api.RegisterAsync(UserName, DisplayName, Password);
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
    private void GoToLogin()
    {
        _nav.NavigateTo(App.Services.GetService(typeof(LoginViewModel))!);
    }
}

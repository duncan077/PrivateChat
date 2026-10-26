using System;
using System.Threading.Tasks;
using ChatApp.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatApp.Ui.ViewModels;

/// <summary>
/// Sign-in screen. Port of <c>Components/Pages/Login.razor</c>.
/// </summary>
public partial class LoginViewModel : ViewModelBase
{
    private readonly ApiClientService _api;
    private readonly ChatClientService _chat;
    private readonly AuthStateProvider _auth;

    [ObservableProperty]
    public partial string UserName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RememberMe { get; set; } = true;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Raised when the user wants the registration screen instead.</summary>
    public event Action? RegisterRequested;

    public LoginViewModel(ApiClientService api, ChatClientService chat, AuthStateProvider auth)
    {
        _api = api;
        _chat = chat;
        _auth = auth;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy) return;

        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Enter your username and password.";
            return;
        }

        IsBusy = true;
        try
        {
            var response = await _api.LoginAsync(UserName.Trim(), Password);

            if (!response.Success || string.IsNullOrWhiteSpace(response.Token) || response.User is null)
            {
                ErrorMessage = string.IsNullOrWhiteSpace(response.Error)
                    ? "Sign-in failed."
                    : response.Error;
                return;
            }

            await _auth.SetAuthAsync(response.Token, response.User, RememberMe);
            await _chat.ConnectAsync();

            Password = string.Empty;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not reach the server: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void GoToRegister() => RegisterRequested?.Invoke();
}

using System;
using System.Threading.Tasks;
using ChatApp.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatApp.Ui.ViewModels;

/// <summary>
/// Account creation. Port of <c>Components/Pages/Register.razor</c>: register, then
/// sign straight in with the returned token rather than bouncing back to the login
/// form to retype everything.
/// </summary>
public partial class RegisterViewModel : ViewModelBase
{
    private readonly ApiClientService _api;
    private readonly ChatClientService _chat;
    private readonly AuthStateProvider _auth;

    [ObservableProperty]
    public partial string UserName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Raised when the user wants to go back to the sign-in screen.</summary>
    public event Action? BackRequested;

    public RegisterViewModel(ApiClientService api, ChatClientService chat, AuthStateProvider auth)
    {
        _api = api;
        _chat = chat;
        _auth = auth;
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (IsBusy) return;

        ErrorMessage = null;

        var userName = UserName.Trim();

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Pick a username and a password.";
            return;
        }

        if (Password != ConfirmPassword)
        {
            ErrorMessage = "The passwords do not match.";
            return;
        }

        IsBusy = true;
        try
        {
            // The display name defaults to the username; the server treats it as free text.
            var displayName = string.IsNullOrWhiteSpace(DisplayName) ? userName : DisplayName.Trim();

            var response = await _api.RegisterAsync(userName, displayName, Password);

            if (!response.Success || string.IsNullOrWhiteSpace(response.Token) || response.User is null)
            {
                ErrorMessage = string.IsNullOrWhiteSpace(response.Error)
                    ? "Registration failed."
                    : response.Error;
                return;
            }

            await _auth.SetAuthAsync(response.Token, response.User, rememberMe: true);
            await _chat.ConnectAsync();

            Password = string.Empty;
            ConfirmPassword = string.Empty;
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
    private void GoBack() => BackRequested?.Invoke();
}

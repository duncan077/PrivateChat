using System;
using System.Threading.Tasks;
using ChatApp.Core.Services;
using ChatApp.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace ChatApp.Ui.ViewModels;

/// <summary>
/// Root shell. Owns the signed-out/signed-in switch that
/// <c>Routes.razor</c> + <c>MainLayout.razor</c> used to handle between them.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly AuthStateProvider _auth;
    private readonly ApiClientService _api;
    private readonly ChatClientService _chat;

    [ObservableProperty]
    public partial ViewModelBase? CurrentView { get; set; }

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial bool IsStartingUp { get; set; } = true;

    public MainViewModel(
        IServiceProvider services,
        AuthStateProvider auth,
        ApiClientService api,
        ChatClientService chat,
        ChatRoomStore rooms)
    {
        _services = services;
        _auth = auth;
        _api = api;
        _chat = chat;

        // Touch the room store so it starts its background refresh loop, matching
        // the eager singleton construction the Blazor DI container did.
        _ = rooms;

        _auth.OnAuthStateChanged += HandleAuthStateChanged;
        _chat.OnConnectionStateChanged += HandleConnectionStateChanged;

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _auth.TryAutoLoginAsync(_api, _chat);
        }
        catch
        {
            // A failed auto-login just means we show the sign-in screen.
        }
        finally
        {
            IsStartingUp = false;
            ShowCurrentRoute();
        }
    }

    private void HandleAuthStateChanged() => ShowCurrentRoute();

    private void HandleConnectionStateChanged(bool connected) => IsConnected = connected;

    private void ShowCurrentRoute()
    {
        if (_auth.IsAuthenticated)
        {
            if (CurrentView is ShellViewModel) return;
            CurrentView = ActivatorUtilities.CreateInstance<ShellViewModel>(_services);
            return;
        }

        // Signed out. The register screen survives auth-state noise; everything else
        // lands on the sign-in form.
        if (CurrentView is LoginViewModel or RegisterViewModel) return;
        ShowLogin();
    }

    private void ShowLogin()
    {
        var login = ActivatorUtilities.CreateInstance<LoginViewModel>(_services);
        login.RegisterRequested += ShowRegister;
        CurrentView = login;
    }

    private void ShowRegister()
    {
        var register = ActivatorUtilities.CreateInstance<RegisterViewModel>(_services);
        register.BackRequested += ShowLogin;
        CurrentView = register;
    }
}

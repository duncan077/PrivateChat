using ChatApp.Core.Abstractions;
using Shared.Models;

namespace ChatApp.Core.Services;

/// <summary>
/// Holds the signed-in identity and the persisted "remember me" session.
/// <para>
/// Ported from the Blazor client. The only change is that MAUI's static
/// <c>Preferences.Default</c>/<c>SecureStorage.Default</c> are now injected as
/// <see cref="IAppPreferences"/> and <see cref="ISecureStore"/>, so the same code
/// runs on the desktop heads and on Android Keystore.
/// </para>
/// </summary>
public class AuthStateProvider
{
    private const string PrefRememberMe = "auth.rememberMe";
    private const string SecureTokenKey = "auth.token";

    private readonly IAppPreferences _preferences;
    private readonly ISecureStore _secureStore;

    public string? Token { get; private set; }
    public UserInfo? CurrentUser { get; private set; }
    public bool IsAuthenticated => Token != null && CurrentUser != null;

    public event Action? OnAuthStateChanged;

    public AuthStateProvider(IAppPreferences preferences, ISecureStore secureStore)
    {
        _preferences = preferences;
        _secureStore = secureStore;
    }

    public void SetAuth(string token, UserInfo user)
    {
        Token = token;
        CurrentUser = user;
        OnAuthStateChanged?.Invoke();
    }

    public async Task SetAuthAsync(string token, UserInfo user, bool rememberMe)
    {
        Token = token;
        CurrentUser = user;

        if (rememberMe)
        {
            _preferences.Set(PrefRememberMe, true);
            try
            {
                await _secureStore.SetAsync(SecureTokenKey, token);
            }
            catch
            {
                // Secure storage may fail on unsupported environments; continue in-memory.
            }
        }
        else
        {
            ClearPersistedSession();
        }

        OnAuthStateChanged?.Invoke();
    }

    public async Task<bool> TryAutoLoginAsync(ApiClientService api, ChatClientService chat)
    {
        if (!_preferences.Get(PrefRememberMe, false))
            return false;

        string? savedToken;
        try
        {
            savedToken = await _secureStore.GetAsync(SecureTokenKey);
        }
        catch
        {
            ClearPersistedSession();
            return false;
        }

        if (string.IsNullOrWhiteSpace(savedToken))
        {
            ClearPersistedSession();
            return false;
        }

        try
        {
            var response = await api.RefreshTokenAsync(savedToken);
            if (!response.Success || string.IsNullOrWhiteSpace(response.Token) || response.User == null)
            {
                ClearAuth();
                return false;
            }

            Token = response.Token;
            CurrentUser = response.User;
            OnAuthStateChanged?.Invoke();

            await _secureStore.SetAsync(SecureTokenKey, response.Token);
            await chat.ConnectAsync();
            return true;
        }
        catch
        {
            ClearAuth();
            return false;
        }
    }

    public void ClearAuth()
    {
        Token = null;
        CurrentUser = null;
        ClearPersistedSession();
        OnAuthStateChanged?.Invoke();
    }

    public void UpdateCurrentUserProfile(string displayName, string? avatarUrl)
    {
        if (CurrentUser == null)
            return;

        CurrentUser.DisplayName = displayName;
        CurrentUser.AvatarUrl = avatarUrl;
        OnAuthStateChanged?.Invoke();
    }

    private void ClearPersistedSession()
    {
        _preferences.Remove(PrefRememberMe);
        try
        {
            _secureStore.Remove(SecureTokenKey);
        }
        catch
        {
            // Ignore secure storage cleanup errors.
        }
    }
}

using Shared.Models;
using Microsoft.Maui.Storage;

namespace ChatAppBlazor.Services;

public class AuthStateProvider
{
    private const string PrefRememberMe = "auth.rememberMe";
    private const string SecureTokenKey = "auth.token";

    public string? Token { get; private set; }
    public UserInfo? CurrentUser { get; private set; }
    public bool IsAuthenticated => Token != null && CurrentUser != null;

    public event Action? OnAuthStateChanged;

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
            Preferences.Default.Set(PrefRememberMe, true);
            try
            {
                await SecureStorage.Default.SetAsync(SecureTokenKey, token);
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
        if (!Preferences.Default.Get(PrefRememberMe, false))
            return false;

        string? savedToken = null;
        try
        {
            savedToken = await SecureStorage.Default.GetAsync(SecureTokenKey);
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

            await SecureStorage.Default.SetAsync(SecureTokenKey, response.Token);
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
        {
            return;
        }

        CurrentUser.DisplayName = displayName;
        CurrentUser.AvatarUrl = avatarUrl;
        OnAuthStateChanged?.Invoke();
    }

    private static void ClearPersistedSession()
    {
        Preferences.Default.Remove(PrefRememberMe);
        try
        {
            SecureStorage.Default.Remove(SecureTokenKey);
        }
        catch
        {
            // Ignore secure storage cleanup errors.
        }
    }
}

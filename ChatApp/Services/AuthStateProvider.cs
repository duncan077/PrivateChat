using Shared.Models;

namespace ChatApp.Services;

public class AuthStateProvider
{
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

    public void ClearAuth()
    {
        Token = null;
        CurrentUser = null;
        OnAuthStateChanged?.Invoke();
    }
}

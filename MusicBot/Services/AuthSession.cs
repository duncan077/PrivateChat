using Shared.Models;

namespace MusicBot.Services;

public sealed class AuthSession
{
    public string? Token { get; private set; }
    public UserInfo? CurrentUser { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token) && CurrentUser != null;

    public void SetAuth(string token, UserInfo user)
    {
        Token = token;
        CurrentUser = user;
    }

    public void Clear()
    {
        Token = null;
        CurrentUser = null;
    }
}


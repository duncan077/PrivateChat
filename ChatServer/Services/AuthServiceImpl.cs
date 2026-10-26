using ChatServer.Auth;
using ChatServer.Data;
using ChatServer.Data.Entities;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Shared.Models;
using Shared.Requests;
using Shared.Responses;
using Shared.Services;

namespace ChatServer.Services;

public class AuthServiceImpl : ServiceBase<IAuthService>, IAuthService
{
    private readonly AppDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly IDistributedCache _cache;

    public AuthServiceImpl(AppDbContext db, JwtTokenService jwt, IDistributedCache cache)
    {
        _db = db;
        _jwt = jwt;
        _cache = cache;
    }

    public async UnaryResult<AuthResponse> LoginAsync(LoginRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserName == request.UserName);
        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return new AuthResponse { Success = false, Error = "Invalid username or password" };
        }

        var token = _jwt.GenerateToken(user);
        return new AuthResponse
        {
            Success = true,
            Token = token,
            User = new UserInfo
            {
                Id = user.Id,
                UserName = user.UserName!,
                DisplayName = user.DisplayName,
                AvatarUrl = user.AvatarUrl,
                IsOnline = true
            }
        };
    }

    public async UnaryResult<AuthResponse> RegisterAsync(RegisterRequest request)
    {
        if (await _db.Users.AnyAsync(u => u.UserName == request.UserName))
        {
            return new AuthResponse { Success = false, Error = "Username already taken" };
        }

        // DisplayName is a bounded column now; UpdateProfileAsync already enforced a
        // limit but registration did not, so this was the one way to write a row longer
        // than the schema allows.
        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (displayName.Length > 64)
        {
            return new AuthResponse { Success = false, Error = "Display name must be 64 characters or fewer" };
        }

        var isFirstUser = !await _db.Users.AnyAsync();

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = request.UserName,
            NormalizedUserName = request.UserName.ToUpperInvariant(),
            DisplayName = displayName,
            GlobalRole = isFirstUser ? UserRole.Owner : UserRole.Member,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            SecurityStamp = Guid.NewGuid().ToString()
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        // The new account has to show up in the member list without waiting out its TTL.
        await CacheKeys.InvalidateServerMembersAsync(_cache);

        var token = _jwt.GenerateToken(user);
        return new AuthResponse
        {
            Success = true,
            Token = token,
            User = new UserInfo
            {
                Id = user.Id,
                UserName = user.UserName,
                DisplayName = user.DisplayName,
                AvatarUrl = user.AvatarUrl,
                IsOnline = true
            }
        };
    }

    public async UnaryResult<AuthResponse> RefreshTokenAsync(string token)
    {
        var principal = _jwt.ValidateToken(token);
        if (principal == null)
        {
            return new AuthResponse { Success = false, Error = "Invalid token" };
        }

        var userIdClaim = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return new AuthResponse { Success = false, Error = "Invalid token" };
        }

        var user = await _db.Users.FindAsync(userId);
        if (user == null)
        {
            return new AuthResponse { Success = false, Error = "User not found" };
        }

        var newToken = _jwt.GenerateToken(user);
        return new AuthResponse
        {
            Success = true,
            Token = newToken,
            User = new UserInfo
            {
                Id = user.Id,
                UserName = user.UserName!,
                DisplayName = user.DisplayName,
                AvatarUrl = user.AvatarUrl,
                IsOnline = true
            }
        };
    }
}

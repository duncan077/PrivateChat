using MagicOnion;
using Shared.Requests;
using Shared.Responses;

namespace Shared.Services;

public interface IAuthService : IService<IAuthService>
{
    UnaryResult<AuthResponse> LoginAsync(LoginRequest request);
    UnaryResult<AuthResponse> RegisterAsync(RegisterRequest request);
    UnaryResult<AuthResponse> RefreshTokenAsync(string token);
}

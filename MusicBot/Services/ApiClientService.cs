using MagicOnion.Client;
using Shared.Models;
using Shared.Requests;
using Shared.Responses;
using Shared.Services;

namespace MusicBot.Services;

public sealed class ApiClientService
{
    private readonly GrpcChannelProvider _channelProvider;

    public ApiClientService(GrpcChannelProvider channelProvider)
    {
        _channelProvider = channelProvider;
    }

    public async Task<AuthResponse> LoginAsync(string userName, string password)
    {
        var authClient = MagicOnionClient.Create<IAuthService>(_channelProvider.GetChannel());
        return await authClient.LoginAsync(new LoginRequest
        {
            UserName = userName,
            Password = password
        });
    }

    public async Task<ChatRoomInfo[]> GetRoomsAsync()
    {
        var roomClient = MagicOnionClient.Create<IChatRoomService>(_channelProvider.GetChannel());
        return await roomClient.GetRoomsAsync();
    }
}


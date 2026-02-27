using MagicOnion.Client;
using Shared.Models;
using Shared.Requests;
using Shared.Responses;
using Shared.Services;

namespace ChatAppBlazor.Services;

public class ApiClientService
{
    private readonly GrpcChannelProvider _channelProvider;

    public ApiClientService(GrpcChannelProvider channelProvider)
    {
        _channelProvider = channelProvider;
    }

    public async Task<AuthResponse> LoginAsync(string userName, string password)
    {
        var client = MagicOnionClient.Create<IAuthService>(_channelProvider.GetChannel());
        return await client.LoginAsync(new LoginRequest { UserName = userName, Password = password });
    }

    public async Task<AuthResponse> RegisterAsync(string userName, string displayName, string password)
    {
        var client = MagicOnionClient.Create<IAuthService>(_channelProvider.GetChannel());
        return await client.RegisterAsync(new RegisterRequest
        {
            UserName = userName,
            DisplayName = displayName,
            Password = password
        });
    }

    public async Task<AuthResponse> RefreshTokenAsync(string token)
    {
        var client = MagicOnionClient.Create<IAuthService>(_channelProvider.GetChannel());
        return await client.RefreshTokenAsync(token);
    }

    public async Task<ChatRoomInfo[]> GetRoomsAsync()
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetRoomsAsync();
    }

    public async Task<ChatRoomInfo> GetRoomAsync(Guid roomId)
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetRoomAsync(roomId);
    }

    public async Task<ChatRoomInfo> CreateRoomAsync(string name, string? description, bool voiceEnabled, bool videoEnabled)
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.CreateRoomAsync(new CreateRoomRequest
        {
            Name = name,
            Description = description,
            IsVoiceEnabled = voiceEnabled,
            IsVideoEnabled = videoEnabled
        });
    }

    public async Task<bool> DeleteRoomAsync(Guid roomId)
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.DeleteRoomAsync(roomId);
    }

    public async Task<ChatMemberInfo[]> GetRoomMembersAsync(Guid roomId)
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetRoomMembersAsync(roomId);
    }

    public async Task<ChatMessage[]> GetRoomMessagesAsync(Guid roomId, int count = 50, DateTime? before = null)
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetRoomMessagesAsync(roomId, count, before);
    }

    public async Task<UserInfo[]> GetContactsAsync()
    {
        var client = MagicOnionClient.Create<IPrivateMessageService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetContactsAsync();
    }

    public async Task<PrivateMessage[]> GetPrivateMessagesAsync(Guid otherUserId, int count = 50, DateTime? before = null)
    {
        var client = MagicOnionClient.Create<IPrivateMessageService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetMessagesAsync(otherUserId, count, before);
    }

    public async Task<AttachmentInfo> GetUploadUrlAsync(string fileName, string contentType, long sizeBytes)
    {
        var client = MagicOnionClient.Create<IFileUploadService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetUploadUrlAsync(fileName, contentType, sizeBytes);
    }
}

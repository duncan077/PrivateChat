using MagicOnion.Client;
using Shared.Models;
using Shared.Requests;
using Shared.Responses;
using Shared.Services;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ChatAppBlazor.Services;

public class ApiClientService
{
    private readonly GrpcChannelProvider _channelProvider;
    private readonly AuthStateProvider _auth;

    public ApiClientService(GrpcChannelProvider channelProvider, AuthStateProvider auth)
    {
        _channelProvider = channelProvider;
        _auth = auth;
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

    public async Task<ServerMemberInfo[]> GetServerMembersAsync()
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetServerMembersAsync();
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

    public async Task<AttachmentInfo> UploadFileAsync(Stream fileStream, string fileName, string contentType, long sizeBytes, CancellationToken cancellationToken = default)
    {
        using var client = CreateAuthorizedHttpClient();
        using var multipart = new MultipartFormDataContent();

        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        multipart.Add(streamContent, "file", fileName);

        using var response = await client.PostAsync("api/upload", multipart, cancellationToken);
        response.EnsureSuccessStatusCode();

        var upload = await response.Content.ReadFromJsonAsync<UploadResponse>(cancellationToken: cancellationToken);
        if (upload == null)
        {
            throw new InvalidOperationException("Upload failed: empty response.");
        }

        return new AttachmentInfo
        {
            Id = upload.Id,
            FileName = string.IsNullOrWhiteSpace(upload.FileName) ? fileName : upload.FileName,
            ContentType = string.IsNullOrWhiteSpace(upload.ContentType) ? contentType : upload.ContentType,
            SizeBytes = upload.SizeBytes > 0 ? upload.SizeBytes : sizeBytes,
            Url = ToAbsoluteUrl(upload.Url)
        };
    }

    public AttachmentInfo[] NormalizeAttachments(AttachmentInfo[] attachments)
    {
        foreach (var attachment in attachments)
        {
            attachment.Url = ToAbsoluteUrl(attachment.Url);
        }

        return attachments;
    }

    public string ToAbsoluteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return url;
        }

        var baseUrl = _channelProvider.ServerUrl.TrimEnd('/');
        var path = url.StartsWith('/') ? url : $"/{url}";
        return $"{baseUrl}{path}";
    }

    private HttpClient CreateAuthorizedHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(_channelProvider.ServerUrl.EndsWith("/") ? _channelProvider.ServerUrl : $"{_channelProvider.ServerUrl}/")
        };

        if (!string.IsNullOrWhiteSpace(_auth.Token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _auth.Token);
        }

        return client;
    }

    private sealed class UploadResponse
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("contentType")]
        public string ContentType { get; set; } = string.Empty;

        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }

}

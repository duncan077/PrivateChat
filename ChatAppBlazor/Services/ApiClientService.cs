using ChatAppBlazor.Serialization;
using MagicOnion.Client;
using Shared.Models;
using Shared.Requests;
using Shared.Responses;
using Shared.Services;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ChatAppBlazor.Services;

public class ApiClientService
{
    private const int MaxPreviewBytes = 262_144;
    private const string PreviewUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
    private static readonly Regex TitleRegex = new(
        "<title[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MetaTagRegex = new(
        "<meta\\s+[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LinkTagRegex = new(
        "<link\\s+[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttributeRegex = new(
        "(?<key>[A-Za-z_:][A-Za-z0-9_:\\-.]*)\\s*=\\s*(?:\"(?<v1>[^\"]*)\"|'(?<v2>[^']*)'|(?<v3>[^\\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    public async Task<ChatRoomInfo> RenameRoomAsync(Guid roomId, string name)
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.RenameRoomAsync(new RenameRoomRequest { RoomId = roomId, Name = name });
    }

    public async Task<ServerMemberInfo[]> GetServerMembersAsync()
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetServerMembersAsync();
    }

    public async Task<RoomCallParticipantsInfo[]> GetRoomCallParticipantsAsync()
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetRoomCallParticipantsAsync();
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

    public async Task<ChatMessage[]> GetRoomMessagePageAsync(Guid roomId, int count = 50, MessageCursor? before = null, MessageCursor? after = null)
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetRoomMessagePageAsync(roomId, count, before, after);
    }

    public async Task<PrivateMessage[]> GetPrivateMessagePageAsync(Guid otherUserId, int count = 50, MessageCursor? before = null, MessageCursor? after = null)
    {
        var client = MagicOnionClient.Create<IPrivateMessageService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetMessagePageAsync(otherUserId, count, before, after);
    }

    public async Task<ChatMessage[]> GetRoomMessagesAroundAsync(Guid roomId, Guid messageId, int contextCount = 15)
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetRoomMessagesAroundAsync(roomId, messageId, contextCount);
    }

    public async Task<CustomAssetInfo[]> GetCustomAssetsAsync()
    {
        var client = MagicOnionClient.Create<IServerAssetService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetAssetsAsync();
    }

    public async Task<CustomAssetInfo> CreateCustomAssetAsync(CustomAssetKind kind, string name, string url)
    {
        var client = MagicOnionClient.Create<IServerAssetService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.CreateAssetAsync(new CreateCustomAssetRequest
        {
            Kind = kind,
            Name = name,
            Url = url
        });
    }

    public async Task<bool> DeleteCustomAssetAsync(Guid assetId)
    {
        var client = MagicOnionClient.Create<IServerAssetService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.DeleteAssetAsync(assetId);
    }

    public async Task<SearchResultInfo[]> SearchRoomMessagesAsync(string query, Guid? roomId = null, DateTime? before = null, int count = 30)
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.SearchMessagesAsync(new SearchMessagesRequest
        {
            Query = query,
            RoomId = roomId,
            Before = before,
            Count = count
        });
    }

    public async Task<PrivateMessage[]> SearchPrivateMessagesAsync(string query, DateTime? before = null, int count = 30)
    {
        var client = MagicOnionClient.Create<IPrivateMessageService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.SearchMessagesAsync(query, count, before);
    }

    public async Task<RoomUnreadState[]> GetRoomUnreadStatesAsync()
    {
        var client = MagicOnionClient.Create<IChatRoomService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetRoomUnreadStatesAsync();
    }

    public async Task<PrivateUnreadInfo[]> GetPrivateUnreadCountsAsync()
    {
        var client = MagicOnionClient.Create<IPrivateMessageService>(
            _channelProvider.GetChannel(), _channelProvider.GetAuthFilters());
        return await client.GetUnreadCountsAsync();
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

        var upload = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.UploadResponse, cancellationToken);
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

    public async Task<LinkPreviewInfo?> GetLinkPreviewAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var sourceUri))
        {
            return null;
        }

        if (!string.Equals(sourceUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (IsYouTubeUrl(sourceUri))
        {
            var ytPreview = await GetYouTubePreviewAsync(sourceUri, cancellationToken);
            if (ytPreview != null) return ytPreview;
        }

        try
        {
            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
            request.Headers.UserAgent.ParseAdd(PreviewUserAgent);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.2");
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var finalUri = response.RequestMessage?.RequestUri ?? sourceUri;
            if (!response.IsSuccessStatusCode)
            {
                return BuildPreviewFallback(finalUri);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return BuildPreviewFallback(finalUri);
            }

            var html = await ReadContentWithLimitAsync(
                response.Content,
                MaxPreviewBytes,
                response.Content.Headers.ContentType?.CharSet,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(html))
            {
                return BuildPreviewFallback(finalUri);
            }

            return ParsePreviewFromHtml(finalUri, html);
        }
        catch
        {
            return BuildPreviewFallback(sourceUri);
        }
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

    internal sealed class UploadResponse
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

    private static async Task<string> ReadContentWithLimitAsync(HttpContent content, int maxBytes, string? charset, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var ms = new MemoryStream(Math.Min(maxBytes, 65_536));
        var buffer = new byte[8192];
        var remaining = maxBytes;

        while (remaining > 0)
        {
            var toRead = Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await ms.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }

        var bytes = ms.ToArray();
        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
            {
                return Encoding.GetEncoding(charset).GetString(bytes);
            }
        }
        catch
        {
            // Fall back to UTF-8 when charset isn't valid.
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static LinkPreviewInfo ParsePreviewFromHtml(Uri sourceUri, string html)
    {
        var openGraph = BuildOpenGraphMap(html);
        var title = TryGetDictionaryValue(openGraph, "og:title")
            ?? FindMetaContent(html, "name", "twitter:title")
            ?? FindTitle(html)
            ?? sourceUri.Host;
        var description = TryGetDictionaryValue(openGraph, "og:description")
            ?? FindMetaContent(html, "name", "description")
            ?? FindMetaContent(html, "name", "twitter:description");
        var siteName = TryGetDictionaryValue(openGraph, "og:site_name") ?? sourceUri.Host;
        var canonical = TryGetDictionaryValue(openGraph, "og:url")
            ?? FindLinkHref(html, "canonical")
            ?? sourceUri.ToString();
        var image =
            TryGetDictionaryValue(openGraph, "og:image:secure_url")
            ?? TryGetDictionaryValue(openGraph, "og:image:url")
            ?? TryGetDictionaryValue(openGraph, "og:image")
            ?? FindMetaContent(html, "name", "twitter:image");
        var icon = FindLinkHref(html, "icon");

        return new LinkPreviewInfo
        {
            Url = sourceUri.ToString(),
            CanonicalUrl = ToAbsoluteUrl(sourceUri, canonical),
            Title = NormalizeText(title),
            Description = NormalizeText(description),
            SiteName = NormalizeText(siteName),
            ImageUrl = ToAbsoluteUrl(sourceUri, image),
            IconUrl = ToAbsoluteUrl(sourceUri, icon)
        };
    }

    private static Dictionary<string, string> BuildOpenGraphMap(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match tag in MetaTagRegex.Matches(html))
        {
            var attrs = ParseAttributes(tag.Value);
            var property =
                (attrs.TryGetValue("property", out var p) ? p : null)
                ?? (attrs.TryGetValue("name", out var n) ? n : null);

            if (string.IsNullOrWhiteSpace(property) || !property.StartsWith("og:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!attrs.TryGetValue("content", out var content) || string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (!result.ContainsKey(property))
            {
                result[property] = WebUtility.HtmlDecode(content);
            }
        }

        return result;
    }

    private static string? TryGetDictionaryValue(IReadOnlyDictionary<string, string> values, string key)
    {
        if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return null;
    }

    private static LinkPreviewInfo BuildPreviewFallback(Uri uri)
    {
        var host = uri.Host;
        return new LinkPreviewInfo
        {
            Url = uri.ToString(),
            CanonicalUrl = uri.ToString(),
            Title = host,
            SiteName = host
        };
    }

    private static bool IsYouTubeUrl(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        return host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "youtu.be";
    }

    private static string? ExtractYouTubeVideoId(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host == "youtu.be")
            return uri.AbsolutePath.TrimStart('/').Split('/')[0];

        var segments = uri.AbsolutePath.TrimStart('/').Split('/');
        if (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed")
            return segments[1];

        return GetUriQueryParam(uri, "v");
    }

    private static string? GetUriQueryParam(Uri uri, string param)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == param)
                return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    private async Task<LinkPreviewInfo?> GetYouTubePreviewAsync(Uri videoUri, CancellationToken cancellationToken)
    {
        try
        {
            var oEmbedUrl = $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(videoUri.ToString())}&format=json";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var request = new HttpRequestMessage(HttpMethod.Get, oEmbedUrl);
            request.Headers.UserAgent.ParseAdd(PreviewUserAgent);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var oembed = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.YouTubeOEmbedResponse, cancellationToken);
            if (oembed == null) return null;

            var videoId = ExtractYouTubeVideoId(videoUri);
            var thumbnail = !string.IsNullOrWhiteSpace(oembed.ThumbnailUrl)
                ? oembed.ThumbnailUrl
                : videoId != null ? $"https://img.youtube.com/vi/{videoId}/hqdefault.jpg" : null;

            return new LinkPreviewInfo
            {
                Url = videoUri.ToString(),
                CanonicalUrl = videoUri.ToString(),
                Title = NormalizeText(oembed.Title) ?? "YouTube Video",
                Description = NormalizeText(oembed.AuthorName != null ? $"by {oembed.AuthorName}" : null),
                SiteName = "YouTube",
                ImageUrl = thumbnail ?? string.Empty,
                IconUrl = "https://www.youtube.com/favicon.ico"
            };
        }
        catch
        {
            return null;
        }
    }

    internal sealed class YouTubeOEmbedResponse
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("author_name")]
        public string? AuthorName { get; set; }

        [JsonPropertyName("thumbnail_url")]
        public string? ThumbnailUrl { get; set; }
    }

    private static string? FindTitle(string html)
    {
        var match = TitleRegex.Match(html);
        if (!match.Success) return null;
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string? FindMetaContent(string html, string attrName, string attrValue)
    {
        foreach (Match tag in MetaTagRegex.Matches(html))
        {
            var attrs = ParseAttributes(tag.Value);
            if (!attrs.TryGetValue(attrName, out var value)) continue;
            if (!string.Equals(value, attrValue, StringComparison.OrdinalIgnoreCase)) continue;
            if (!attrs.TryGetValue("content", out var content)) continue;
            return WebUtility.HtmlDecode(content);
        }

        return null;
    }

    private static string? FindLinkHref(string html, string relContains)
    {
        foreach (Match tag in LinkTagRegex.Matches(html))
        {
            var attrs = ParseAttributes(tag.Value);
            if (!attrs.TryGetValue("rel", out var rel)) continue;
            if (rel.IndexOf(relContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!attrs.TryGetValue("href", out var href)) continue;
            return WebUtility.HtmlDecode(href);
        }

        return null;
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributeRegex.Matches(tag))
        {
            var key = match.Groups["key"].Value;
            if (string.IsNullOrWhiteSpace(key)) continue;
            var value = match.Groups["v1"].Success
                ? match.Groups["v1"].Value
                : match.Groups["v2"].Success
                    ? match.Groups["v2"].Value
                    : match.Groups["v3"].Value;
            result[key] = value;
        }

        return result;
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = Regex.Replace(value, "\\s+", " ").Trim();
        if (normalized.Length > 320)
        {
            normalized = normalized[..320] + "...";
        }

        return normalized;
    }

    private static string? ToAbsoluteUrl(Uri sourceUri, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(sourceUri, candidate, out var combined))
        {
            return combined.ToString();
        }

        return null;
    }

}

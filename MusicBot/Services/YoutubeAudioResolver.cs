using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MusicBot.Services;

public sealed class YoutubeAudioResolver
{
    private readonly CookieContainer _cookieContainer = new();
    private readonly HttpClient _youtubeHttpClient;
    private readonly HttpClient _youtubeNoCookieHttpClient;
    private readonly object _youtubeClientGate = new();
    private YoutubeClient _youtube;
    private readonly YoutubeClient _youtubeNoCookie;
    private string? _externalVerifierUrl;
    private string? _externalVerifierApiKey;
    private static readonly HashSet<string> IgnoredHeaderCookieAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "path",
        "domain",
        "expires",
        "max-age",
        "samesite",
        "secure",
        "httponly",
        "priority",
        "partitioned"
    };
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public YoutubeAudioResolver()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = true,
            CookieContainer = _cookieContainer,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        _youtubeHttpClient = new HttpClient(handler, disposeHandler: true);
        _youtubeHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        var noCookieHandler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        _youtubeNoCookieHttpClient = new HttpClient(noCookieHandler, disposeHandler: true);
        _youtubeNoCookieHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        _youtube = new YoutubeClient(_youtubeHttpClient);
        _youtubeNoCookie = new YoutubeClient(_youtubeNoCookieHttpClient);
        RebuildYoutubeClient();
    }

    public void ConfigureExternalVerifier(string? verifierUrl, string? apiKey)
    {
        _externalVerifierUrl = string.IsNullOrWhiteSpace(verifierUrl) ? null : verifierUrl.Trim();
        _externalVerifierApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public void ClearCookies()
    {
        foreach (var uri in new[]
                 {
                     new Uri("https://youtube.com"),
                     new Uri("https://www.youtube.com"),
                     new Uri("https://music.youtube.com"),
                     new Uri("https://google.com"),
                     new Uri("https://www.google.com")
                 })
        {
            foreach (Cookie cookie in _cookieContainer.GetCookies(uri))
            {
                cookie.Expired = true;
            }
        }

        RebuildYoutubeClient();
    }

    public int ImportCookies(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return 0;
        var text = rawText.Trim();

        var count = 0;
        if (text.StartsWith("[") || text.StartsWith("{"))
        {
            count = ImportJsonCookies(text);
            if (count > 0) RebuildYoutubeClient();
            return count;
        }

        if (text.Contains('\t'))
        {
            count = ImportNetscapeCookies(text);
            if (count > 0) RebuildYoutubeClient();
            return count;
        }

        count = ImportHeaderCookies(text);
        if (count > 0) RebuildYoutubeClient();
        return count;
    }

    public async Task<ResolvedTrack?> ResolveAsync(string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var query = input.Trim();
        var hasCookies = HasImportedCookies();
        var primaryAttempt = await ResolveWithClientContextAsync(query, includeCookies: hasCookies, cancellationToken);
        if (primaryAttempt.Track != null)
        {
            return primaryAttempt.Track;
        }

        var localResolveError = primaryAttempt.Error;

        // If imported cookies are stale/invalid, retry once without them before failing.
        if (hasCookies && IsUnauthorizedFailure(localResolveError))
        {
            var noCookieAttempt = await ResolveWithClientContextAsync(query, includeCookies: false, cancellationToken);
            if (noCookieAttempt.Track != null)
            {
                return noCookieAttempt.Track;
            }

            localResolveError = noCookieAttempt.Error ?? localResolveError;
        }

        if (localResolveError != null)
        {
            var externalTrack = await ResolveViaExternalVerifierAsync(query, localResolveError, cancellationToken);
            if (externalTrack != null)
            {
                return externalTrack;
            }

            throw new InvalidOperationException(
                $"YouTube resolve failed ({localResolveError.Message}). Import cookies or configure external anti-bot verifier.",
                localResolveError);
        }

        return null;
    }

    private async Task<(ResolvedTrack? Track, Exception? Error)> ResolveWithClientContextAsync(
        string query,
        bool includeCookies,
        CancellationToken cancellationToken)
    {
        var youtube = GetYoutubeClient(includeCookies);
        var httpClient = GetYoutubeHttpClient(includeCookies);
        ResolvedTrack? track = null;
        Exception? localResolveError = null;

        var videoId = VideoId.TryParse(query);
        if (videoId.HasValue)
        {
            try
            {
                track = await ResolveTrackFromVideoIdAsync(
                    videoId.Value,
                    null,
                    null,
                    null,
                    youtube,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                localResolveError = ex;
            }
        }
        else
        {
            track = await ResolveFromSearchQueryAsync(
                query,
                youtube,
                cancellationToken,
                ex => localResolveError ??= ex);

            if (track == null)
            {
                var fallbackVideoId = await ResolveSearchVideoIdViaHtmlAsync(query, httpClient, cancellationToken);
                if (fallbackVideoId.HasValue)
                {
                    try
                    {
                        track = await ResolveTrackFromVideoIdAsync(
                            fallbackVideoId.Value,
                            null,
                            null,
                            null,
                            youtube,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        localResolveError ??= ex;
                    }
                }
            }
        }

        return (track, localResolveError);
    }

    private async Task<ResolvedTrack?> ResolveFromSearchQueryAsync(
        string query,
        YoutubeClient youtube,
        CancellationToken cancellationToken,
        Action<Exception> onCandidateError)
    {
        const int maxCandidates = 8;
        var tried = 0;

        try
        {
            await foreach (var result in youtube.Search.GetVideosAsync(query, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(result.Id.Value))
                {
                    continue;
                }

                tried++;
                try
                {
                    var resolved = await ResolveTrackFromVideoIdAsync(
                        result.Id,
                        result.Title,
                        result.Author.ChannelTitle,
                        result.Duration,
                        youtube,
                        cancellationToken);
                    if (resolved != null)
                    {
                        return resolved;
                    }
                }
                catch (Exception ex)
                {
                    onCandidateError(ex);
                }

                if (tried >= maxCandidates)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            onCandidateError(ex);
        }

        return null;
    }

    private async Task<ResolvedTrack?> ResolveTrackFromVideoIdAsync(
        VideoId videoId,
        string? hintTitle,
        string? hintAuthor,
        TimeSpan? hintDuration,
        YoutubeClient youtube,
        CancellationToken cancellationToken)
    {
        var manifest = await youtube.Videos.Streams.GetManifestAsync(videoId, cancellationToken);
        var audioOnlyCandidates = manifest
            .GetAudioOnlyStreams()
            .OrderByDescending(s => ScoreContainer(s.Container.Name))
            .ThenByDescending(s => s.Bitrate.BitsPerSecond)
            .Select(s => new CandidateStream(
                s.Url,
                s.Container.Name,
                s.Bitrate.BitsPerSecond))
            .Take(6)
            .ToList();

        // Some platforms reject WebM/Opus audio-only streams. Add MP4 muxed fallback URLs.
        var muxedCandidates = manifest
            .GetMuxedStreams()
            .Where(s =>
                string.Equals(s.Container.Name, "mp4", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Container.Name, "m4a", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => ScoreContainer(s.Container.Name))
            .ThenByDescending(s => s.Bitrate.BitsPerSecond)
            .Select(s => new CandidateStream(
                s.Url,
                s.Container.Name,
                s.Bitrate.BitsPerSecond))
            .Take(4)
            .ToList();

        var candidateStreams = audioOnlyCandidates
            .Concat(muxedCandidates)
            .DistinctBy(s => s.Url)
            .ToList();
        if (candidateStreams.Count == 0)
        {
            return null;
        }

        var selectedStream = candidateStreams[0];
        var candidateUrls = candidateStreams
            .Select(s => s.Url)
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string? title = string.IsNullOrWhiteSpace(hintTitle) ? null : hintTitle.Trim();
        string? author = string.IsNullOrWhiteSpace(hintAuthor) ? null : hintAuthor.Trim();
        var duration = hintDuration ?? TimeSpan.Zero;

        if (title == null || author == null || duration <= TimeSpan.Zero)
        {
            var video = await youtube.Videos.GetAsync(videoId, cancellationToken);
            title ??= video.Title;
            author ??= video.Author.ChannelTitle;
            if (duration <= TimeSpan.Zero)
            {
                duration = video.Duration ?? TimeSpan.Zero;
            }
        }

        return new ResolvedTrack(
            videoId.Value,
            title ?? videoId.Value,
            author ?? "YouTube",
            duration,
            selectedStream.Url,
            selectedStream.Container,
            selectedStream.BitrateBps,
            candidateUrls
        );
    }

    private async Task<VideoId?> ResolveSearchVideoIdViaHtmlAsync(
        string query,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        try
        {
            var searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            request.Headers.TryAddWithoutValidation("accept-language", "en-US,en;q=0.9");

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            // Prefer JSON payload first, then fallback to watch URL pattern.
            var jsonMatch = Regex.Match(html, "\"videoId\":\"([A-Za-z0-9_-]{11})\"");
            var watchMatch = jsonMatch.Success
                ? jsonMatch
                : Regex.Match(html, "watch\\?v=([A-Za-z0-9_-]{11})");
            if (!watchMatch.Success)
            {
                return null;
            }

            return VideoId.TryParse(watchMatch.Groups[1].Value);
        }
        catch
        {
            return null;
        }
    }

    private int ImportNetscapeCookies(string text)
    {
        var count = 0;
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

            var parts = trimmed.Split('\t');
            if (parts.Length < 7) continue;

            var domain = parts[0].Trim();
            if (domain.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
            {
                domain = domain["#HttpOnly_".Length..];
            }
            var path = string.IsNullOrWhiteSpace(parts[2]) ? "/" : parts[2].Trim();
            var secure = string.Equals(parts[3], "TRUE", StringComparison.OrdinalIgnoreCase);
            var name = parts[5].Trim();
            var value = parts[6].Trim();
            DateTime? expiresUtc = null;
            if (long.TryParse(parts[4], out var unix) && unix > 0)
            {
                expiresUtc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            }

            if (TryAddCookie(domain, path, name, value, secure, expiresUtc))
            {
                count++;
            }
        }

        return count;
    }

    private int ImportHeaderCookies(string text)
    {
        var count = 0;

        var normalizedNewLines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalizedNewLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length > 1)
        {
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("set-cookie:", StringComparison.OrdinalIgnoreCase))
                {
                    line = line["set-cookie:".Length..].Trim();
                }
                else if (line.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
                {
                    line = line["cookie:".Length..].Trim();
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var pairCandidate = line.Split(';', 2, StringSplitOptions.TrimEntries)[0];
                if (!TryParseHeaderCookiePair(pairCandidate, out var name, out var value))
                {
                    continue;
                }

                if (TryAddCookie(".youtube.com", "/", name, value, secure: true, expiresUtc: null))
                {
                    count++;
                }
            }

            return count;
        }

        var normalized = text
            .Replace("\r\n", ";", StringComparison.Ordinal)
            .Replace('\n', ';')
            .Replace('\r', ';')
            .Trim();
        if (normalized.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["cookie:".Length..].Trim();
        }
        else if (normalized.StartsWith("set-cookie:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["set-cookie:".Length..].Trim();
        }

        var chunks = normalized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var chunk in chunks)
        {
            if (!TryParseHeaderCookiePair(chunk, out var name, out var value))
            {
                continue;
            }

            if (IgnoredHeaderCookieAttributes.Contains(name))
            {
                continue;
            }

            if (TryAddCookie(".youtube.com", "/", name, value, secure: true, expiresUtc: null))
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryParseHeaderCookiePair(string input, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;

        var idx = input.IndexOf('=');
        if (idx <= 0 || idx >= input.Length - 1)
        {
            return false;
        }

        name = input[..idx].Trim();
        if (name.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
        {
            name = name["cookie:".Length..].Trim();
        }
        else if (name.StartsWith("set-cookie:", StringComparison.OrdinalIgnoreCase))
        {
            name = name["set-cookie:".Length..].Trim();
        }

        value = input[(idx + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return true;
    }

    private int ImportJsonCookies(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                return ImportJsonCookieArray(root);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            if (TryGetPropertyAny(root, out var cookiesElement, "cookies", "Cookies")
                && cookiesElement.ValueKind == JsonValueKind.Array)
            {
                return ImportJsonCookieArray(cookiesElement);
            }

            return TryImportJsonCookie(root) ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    private int ImportJsonCookieArray(JsonElement arrayElement)
    {
        var count = 0;
        foreach (var cookieElement in arrayElement.EnumerateArray())
        {
            if (TryImportJsonCookie(cookieElement))
            {
                count++;
            }
        }

        return count;
    }

    private bool TryImportJsonCookie(JsonElement cookieElement)
    {
        if (cookieElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var name = ReadString(cookieElement, "name", "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var value = ReadString(cookieElement, "value", "Value") ?? string.Empty;
        var domain = ReadString(cookieElement, "domain", "Domain", "host", "Host") ?? ".youtube.com";
        var path = ReadString(cookieElement, "path", "Path") ?? "/";
        var secure = ReadBool(cookieElement, "secure", "Secure");
        var expiresUtc = ReadExpiryUtc(cookieElement);

        return TryAddCookie(domain, path, name.Trim(), value, secure, expiresUtc);
    }

    private bool TryAddCookie(string domain, string path, string name, string value, bool secure, DateTime? expiresUtc)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var normalizedDomain = domain.Trim();
            if (!normalizedDomain.StartsWith(".", StringComparison.Ordinal))
            {
                normalizedDomain = "." + normalizedDomain;
            }

            var cookie = new Cookie(name, value ?? string.Empty, path ?? "/", normalizedDomain)
            {
                Secure = secure,
                HttpOnly = false
            };
            if (expiresUtc.HasValue)
            {
                cookie.Expires = expiresUtc.Value;
            }

            _cookieContainer.Add(cookie);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement source, params string[] propertyNames)
    {
        if (!TryGetPropertyAny(source, out var value, propertyNames))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool ReadBool(JsonElement source, params string[] propertyNames)
    {
        if (!TryGetPropertyAny(source, out var value, propertyNames))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var intValue) && intValue != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static DateTime? ReadExpiryUtc(JsonElement source)
    {
        if (!TryGetPropertyAny(source, out var expiresElement,
                "expiresUtc", "ExpiresUtc", "expires", "Expires", "expirationDate", "ExpirationDate",
                "expiresUnixSeconds", "ExpiresUnixSeconds"))
        {
            return null;
        }

        if (expiresElement.ValueKind == JsonValueKind.Number)
        {
            if (expiresElement.TryGetInt64(out var seconds))
            {
                return UnixSecondsToUtc(seconds);
            }

            if (expiresElement.TryGetDouble(out var doubleSeconds))
            {
                return UnixSecondsToUtc((long)Math.Truncate(doubleSeconds));
            }
        }

        if (expiresElement.ValueKind == JsonValueKind.String)
        {
            var text = expiresElement.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            {
                return UnixSecondsToUtc(unixSeconds);
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var unixDouble))
            {
                return UnixSecondsToUtc((long)Math.Truncate(unixDouble));
            }

            if (DateTime.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsedDate))
            {
                return parsedDate;
            }
        }

        return null;
    }

    private static DateTime? UnixSecondsToUtc(long unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetPropertyAny(JsonElement source, out JsonElement value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (source.TryGetProperty(propertyName, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private YoutubeClient GetYoutubeClient(bool includeCookies)
    {
        if (!includeCookies)
        {
            return _youtubeNoCookie;
        }

        lock (_youtubeClientGate)
        {
            return _youtube;
        }
    }

    private HttpClient GetYoutubeHttpClient(bool includeCookies)
        => includeCookies ? _youtubeHttpClient : _youtubeNoCookieHttpClient;

    private bool HasImportedCookies()
        => SnapshotCookies().Count > 0;

    private static bool IsUnauthorizedFailure(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is HttpRequestException httpEx && httpEx.StatusCode == HttpStatusCode.Unauthorized)
            {
                return true;
            }

            var message = current.Message;
            if (message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void RebuildYoutubeClient()
    {
        var cookies = SnapshotCookies();
        lock (_youtubeClientGate)
        {
            _youtube = cookies.Count == 0
                ? new YoutubeClient(_youtubeHttpClient)
                : new YoutubeClient(_youtubeHttpClient, cookies);
        }
    }

    private IReadOnlyList<Cookie> SnapshotCookies()
    {
        var result = new List<Cookie>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var uri in new[]
                 {
                     new Uri("https://youtube.com"),
                     new Uri("https://www.youtube.com"),
                     new Uri("https://music.youtube.com"),
                     new Uri("https://m.youtube.com"),
                     new Uri("https://youtu.be"),
                     new Uri("https://google.com"),
                     new Uri("https://www.google.com")
                 })
        {
            foreach (Cookie cookie in _cookieContainer.GetCookies(uri))
            {
                if (cookie.Expired)
                {
                    continue;
                }

                var key = $"{cookie.Name}|{cookie.Domain}|{cookie.Path}";
                if (!unique.Add(key))
                {
                    continue;
                }

                var clone = new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
                {
                    Secure = cookie.Secure,
                    HttpOnly = cookie.HttpOnly
                };
                if (cookie.Expires != DateTime.MinValue)
                {
                    clone.Expires = cookie.Expires;
                }

                result.Add(clone);
            }
        }

        return result;
    }

    private async Task<ResolvedTrack?> ResolveViaExternalVerifierAsync(
        string query,
        Exception? localError,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_externalVerifierUrl))
        {
            return null;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        using var request = new HttpRequestMessage(HttpMethod.Post, _externalVerifierUrl)
        {
            Content = JsonContent.Create(new ExternalResolveRequest
            {
                Query = query,
                LocalError = localError?.Message,
                RequestedAtUtc = DateTime.UtcNow
            })
        };

        if (!string.IsNullOrWhiteSpace(_externalVerifierApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", _externalVerifierApiKey);
            request.Headers.TryAddWithoutValidation("authorization", $"Bearer {_externalVerifierApiKey}");
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<ExternalResolveResponse>(_jsonOptions, cancellationToken);
        if (result == null || string.IsNullOrWhiteSpace(result.StreamUrl))
        {
            return null;
        }

        return new ResolvedTrack(
            result.VideoId ?? string.Empty,
            string.IsNullOrWhiteSpace(result.Title) ? query : result.Title!,
            result.Author ?? "ExternalVerifier",
            TimeSpan.FromSeconds(Math.Max(0, result.DurationSeconds ?? 0)),
            result.StreamUrl!,
            result.Container ?? "unknown",
            result.BitrateBps ?? 0,
            [result.StreamUrl!]
        );
    }

    private static int ScoreContainer(string? containerName)
    {
        var normalized = (containerName ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "mp4" => 300,
            "m4a" => 300,
            "webm" => 200,
            _ => 100
        };
    }

    private sealed class ExternalResolveRequest
    {
        public string Query { get; set; } = string.Empty;
        public string? LocalError { get; set; }
        public DateTime RequestedAtUtc { get; set; }
    }

    private sealed class ExternalResolveResponse
    {
        public string? VideoId { get; set; }
        public string? Title { get; set; }
        public string? Author { get; set; }
        public int? DurationSeconds { get; set; }
        public string? StreamUrl { get; set; }
        public string? Container { get; set; }
        public long? BitrateBps { get; set; }
    }

    private sealed record CandidateStream(
        string Url,
        string Container,
        long BitrateBps);
}

public sealed record ResolvedTrack(
    string VideoId,
    string Title,
    string Author,
    TimeSpan Duration,
    string StreamUrl,
    string Container,
    long BitrateBps,
    IReadOnlyList<string> StreamUrls
);

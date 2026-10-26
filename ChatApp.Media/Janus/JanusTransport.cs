using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ChatApp.Media.Janus;

/// <summary>
/// Typed client for <c>ChatServer</c>'s <c>/api/janus/*</c> endpoints, which proxy the
/// real Janus instance.
/// <para>
/// Signalling deliberately still goes through ChatServer rather than straight to Janus:
/// the server holds the Janus admin credentials, owns room lifecycle, and already
/// implements the long-poll fan-out. This is a direct port of the <c>apiPost</c>/
/// <c>apiGet</c> calls in <c>webrtc-manager.js</c>.
/// </para>
/// </summary>
public sealed class JanusTransport : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Long-poll holds the connection open until Janus has an event, so it needs a
    /// timeout well above the server's poll window rather than HttpClient's default.
    /// </summary>
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(65);

    public JanusTransport(string serverBaseUrl, string authToken, HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true
        });

        _http.BaseAddress = new Uri(NormalizeBaseUrl(serverBaseUrl), UriKind.Absolute);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

        // Individual calls set their own timeouts via CancellationToken; the ambient
        // one only needs to be permissive enough for the long poll.
        _http.Timeout = PollTimeout;
    }

    public async Task<long> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        var response = await PostAsync("api/janus/session/create", content: null, cancellationToken);
        return response.GetProperty("sessionId").GetInt64();
    }

    public async Task DestroySessionAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/janus/session/{sessionId}");
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Attaches a fresh videoroom handle. Each subscriber feed needs its own.</summary>
    public async Task<long> AttachHandleAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        var response = await PostAsync($"api/janus/session/{sessionId}/attach", content: null, cancellationToken);
        return response.GetProperty("handleId").GetInt64();
    }

    /// <summary>
    /// Sends a plugin message and waits for the real event response, not just the ack.
    /// Use for the publisher handshake, where the answer is needed inline.
    /// </summary>
    public Task<JsonElement> SendMessageAsync(
        long sessionId,
        long handleId,
        object body,
        Jsep? jsep = null,
        CancellationToken cancellationToken = default)
        => PostAsync(
            $"api/janus/session/{sessionId}/handle/{handleId}/message",
            BuildMessage(body, jsep),
            cancellationToken);

    /// <summary>
    /// Fire-and-forget: returns as soon as Janus acks, with the real response arriving
    /// on the long-poll channel.
    /// <para>
    /// Subscriber traffic must use this. Waiting inline competes with the poll loop for
    /// the session's single event queue and deadlocks subscribes — the same reason the
    /// JS client had a <c>message-nowait</c> path.
    /// </para>
    /// </summary>
    public Task<JsonElement> SendMessageNoWaitAsync(
        long sessionId,
        long handleId,
        object body,
        Jsep? jsep = null,
        CancellationToken cancellationToken = default)
        => PostAsync(
            $"api/janus/session/{sessionId}/handle/{handleId}/message-nowait",
            BuildMessage(body, jsep),
            cancellationToken);

    public Task TrickleAsync(
        long sessionId,
        long handleId,
        string candidate,
        string? sdpMid,
        int sdpMLineIndex,
        CancellationToken cancellationToken = default)
        => PostAsync(
            $"api/janus/session/{sessionId}/handle/{handleId}/trickle",
            new { candidate, sdpMid, sdpMLineIndex },
            cancellationToken);

    /// <summary>Signals end-of-candidates by posting a literal JSON null.</summary>
    public async Task TrickleCompletedAsync(
        long sessionId,
        long handleId,
        CancellationToken cancellationToken = default)
    {
        using var content = new StringContent("null", Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(
            $"api/janus/session/{sessionId}/handle/{handleId}/trickle", content, cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Blocks until Janus emits an event for this session, or the poll window closes.
    /// </summary>
    /// <exception cref="JanusSessionLostException">
    /// Janus no longer knows the session; the caller must re-join to get new credentials.
    /// </exception>
    public async Task<JsonElement> PollAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            $"api/janus/session/{sessionId}/poll",
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new JanusSessionLostException(sessionId);

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, cancellationToken);
    }

    public async Task KeepaliveAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(
            $"api/janus/session/{sessionId}/keepalive", content: null, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new JanusSessionLostException(sessionId);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Fallback publisher discovery. Runs against a temporary server-side session, so it
    /// is safe to call while our own poll loop is running.
    /// </summary>
    public async Task<IReadOnlyList<JanusRemotePublisher>> ListParticipantsAsync(
        long roomId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/janus/room/{roomId}/participants", cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await ReadJsonAsync(response, cancellationToken);
        return JanusEventParser.ParseParticipants(json);
    }

    private static object BuildMessage(object body, Jsep? jsep)
        => jsep is null ? new { body } : new { body, jsep };

    private async Task<JsonElement> PostAsync(string path, object? content, CancellationToken cancellationToken)
    {
        using var response = content is null
            ? await _http.PostAsync(path, content: null, cancellationToken)
            : await _http.PostAsJsonAsync(path, content, JsonOptions, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new JanusSessionLostException(0);

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, cancellationToken);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
            return default;

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private static string NormalizeBaseUrl(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            throw new ArgumentException($"'{raw}' is not an absolute server URL.", nameof(raw));

        return uri.GetLeftPart(UriPartial.Authority) + "/";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}

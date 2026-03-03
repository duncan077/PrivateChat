using System.Net;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace ChatServer.Services;

/// <summary>
/// Wraps the Janus HTTP API. Key behaviour: Janus is fully async — most POST
/// responses are {"janus":"ack"}, and the real result arrives on the event
/// queue (GET /{sessionId}?maxev=1). We therefore poll after every plugin
/// message until we receive the matching event for our transaction ID.
/// </summary>
public class JanusService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JanusService> _logger;
    private readonly string _janusUrl;
    private readonly Uri _janusUri;
    private readonly bool _useWebSocket;
    private readonly string? _httpFallbackJanusUrl;
    private readonly string? _wsSubProtocol;
    private readonly string? _wsOrigin;
    private readonly string? _wsUserAgent;
    private volatile bool _forceHttpFallback;

    private readonly SemaphoreSlim _wsConnectLock = new(1, 1);
    private readonly SemaphoreSlim _wsSendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _wsPendingTransactions
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, Channel<JsonElement>> _wsSessionEvents = new();
    private readonly ConcurrentDictionary<long, byte> _wsMissingSessions = new();
    private ClientWebSocket? _wsClient;
    private CancellationTokenSource? _wsCts;
    private Task? _wsReceiveLoopTask;

    // How long to wait for an event response after an ack, in total.
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(15);
    // Individual long-poll timeout sent to Janus.
    private const int LongPollSeconds = 10;

    public JanusService(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<JanusService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var rawJanusUrl = config["Janus:Url"] ?? "http://localhost:8088/janus";
        if (!Uri.TryCreate(rawJanusUrl, UriKind.Absolute, out var parsed))
            throw new InvalidOperationException($"Invalid Janus:Url '{rawJanusUrl}'");

        _janusUri = parsed;
        _janusUrl = parsed.ToString().TrimEnd('/');
        _useWebSocket = parsed.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
                        || parsed.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);

        _wsSubProtocol = config["Janus:WsSubProtocol"];
        if (string.IsNullOrWhiteSpace(_wsSubProtocol) && _useWebSocket)
            _wsSubProtocol = "janus-protocol";
        _wsOrigin = config["Janus:WsOrigin"];
        _wsUserAgent = config["Janus:WsUserAgent"];

        var configuredHttpFallback = config["Janus:HttpFallbackUrl"];
        if (_useWebSocket)
        {
            if (!string.IsNullOrWhiteSpace(configuredHttpFallback))
            {
                if (!Uri.TryCreate(configuredHttpFallback, UriKind.Absolute, out var fallbackUri))
                    throw new InvalidOperationException($"Invalid Janus:HttpFallbackUrl '{configuredHttpFallback}'");

                var isHttpScheme = fallbackUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                                   || fallbackUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
                if (!isHttpScheme)
                    throw new InvalidOperationException(
                        $"Janus:HttpFallbackUrl must use http/https, got '{fallbackUri.Scheme}'");

                _httpFallbackJanusUrl = fallbackUri.ToString().TrimEnd('/');
            }
            else
            {
                var builder = new UriBuilder(_janusUri)
                {
                    Scheme = _janusUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http"
                };
                _httpFallbackJanusUrl = builder.Uri.ToString().TrimEnd('/');
            }
        }
    }

    // ──────────────────────────────────────────────
    // Session / handle lifecycle
    // ──────────────────────────────────────────────

    public async Task<long> CreateSessionAsync()
    {
        var txn = NewTxn();
        var resp = await PostAsync(_janusUrl, new
        {
            janus = "create",
            transaction = txn
        });
        // "create" is synchronous — Janus returns the id directly (no ack).
        return resp.GetProperty("data").GetProperty("id").GetInt64();
    }

    public async Task<long> AttachPluginAsync(long sessionId, string plugin = "janus.plugin.videoroom")
    {
        var txn = NewTxn();
        var resp = await PostAsync($"{_janusUrl}/{sessionId}", new
        {
            janus = "attach",
            plugin,
            transaction = txn
        });
        // "attach" is also synchronous.
        return resp.GetProperty("data").GetProperty("id").GetInt64();
    }

    public async Task DestroySessionAsync(long sessionId)
    {
        try
        {
            await PostAsync($"{_janusUrl}/{sessionId}", new
            {
                janus = "destroy",
                transaction = NewTxn()
            });
        }
        catch { /* best effort */ }
    }

    public async Task DetachHandleAsync(long sessionId, long handleId)
    {
        try
        {
            await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", new
            {
                janus = "detach",
                transaction = NewTxn()
            });
        }
        catch { /* best effort */ }
    }

    // ──────────────────────────────────────────────
    // Video room management
    // ──────────────────────────────────────────────

    /// <summary>
    /// Ensures a videoroom exists. Uses a dedicated temporary handle so the
    /// caller's publisher handle is not polluted with admin messages.
    /// Always tries "create"; if the room already exists Janus returns error
    /// code 427 which we silently ignore.
    /// </summary>
    public async Task EnsureVideoRoomAsync(long sessionId, long roomId)
    {
        // Attach a temporary handle for room administration only.
        var adminHandle = await AttachPluginAsync(sessionId);
        try
        {
            var createResult = await SendPluginMessageAndWaitAsync(
                sessionId,
                adminHandle,
                new
                {
                    request = "create",
                    room = roomId,
                    description = $"Room {roomId}",
                    publishers = 20,
                    bitrate = 0, // 0 = no server-side cap; per-publisher bitrate controlled by client
                    // Prefer H264 and allow VP8 only as fallback.
                    // IMPORTANT: Janus expects comma-separated codec names without spaces.
                    videocodec = "h265,h264,vp9,vp8",
                    audiocodec = "opus,pcmu",
                    is_private = false,
                    permanent = false,
                    remb = false,
                    fir_freq = 0,
                    rtx= true,
                });

            if (TryGetPluginError(createResult, out var createErrorCode, out var createErrorText))
            {
                // 427 = room already exists; this is the expected no-op path.
                if (createErrorCode == 427)
                {
                    _logger.LogDebug("Videoroom {Room} already exists", roomId);
                    return;
                }

                throw new InvalidOperationException(
                    $"Janus create room failed for room {roomId}: " +
                    $"error_code={createErrorCode?.ToString() ?? "unknown"}, error={createErrorText ?? "unknown"}");
            }

            _logger.LogDebug("Videoroom {Room} ensured", roomId);
        }
        finally
        {
            await DetachHandleAsync(sessionId, adminHandle);
        }
    }

    // ──────────────────────────────────────────────
    // Plugin messages (async ack → event pattern)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Sends a plugin message and waits for the matching event from Janus.
    /// Returns the full event element (contains plugindata, jsep, etc.).
    /// </summary>
    public async Task<JsonElement> SendPluginMessageAndWaitAsync(
        long sessionId,
        long handleId,
        object body,
        object? jsep = null,
        bool requireJsep = false,
        string? requiredPluginDataField = null)
    {
        var txn = NewTxn();
        var msg = new Dictionary<string, object>
        {
            ["janus"] = "message",
            ["transaction"] = txn,
            ["body"] = body
        };
        if (jsep != null)
            msg["jsep"] = jsep;

        _logger.LogDebug("Sending plugin message to session {Session}/handle {Handle}, txn={Txn}",
            sessionId, handleId, txn);

        var immediate = await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", msg);

        // If we got a complete direct response (not just ack/partial), return it.
        if (IsExpectedPluginResponse(immediate, requireJsep, requiredPluginDataField, out var immediateReason))
        {
            _logger.LogDebug("Got complete direct response for txn={Txn}", txn);
            return immediate;
        }

        // Otherwise poll until we see a matching event for our handle/transaction.
        _logger.LogDebug("Direct response for txn={Txn} was not final ({Reason}), polling for event...",
            txn, immediateReason);
        var deadline = DateTime.UtcNow + EventTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var ev = await RawPollAsync(sessionId);
            if (ev == null) continue;
            if (!IsMatchingPluginResponse(ev.Value, txn, handleId))
                continue;

            if (IsExpectedPluginResponse(ev.Value, requireJsep, requiredPluginDataField, out var reason))
            {
                _logger.LogDebug("Matched final plugin response for txn={Txn}", txn);
                return ev.Value;
            }

            _logger.LogDebug("Skipping partial plugin response for txn={Txn}: {Reason}", txn, reason);
        }

        throw new TimeoutException($"Janus did not respond to transaction {txn} within {EventTimeout.TotalSeconds}s");
    }

    /// <summary>
    /// Sends a plugin message and returns immediately with the Janus ACK
    /// (does NOT poll for the async event response). The caller is expected
    /// to receive the real response via the long-poll event loop.
    /// This avoids event-queue competition with the JS polling loop.
    /// </summary>
    public async Task<JsonElement> SendPluginMessageNoWaitAsync(
        long sessionId,
        long handleId,
        object body,
        object? jsep = null)
    {
        var txn = NewTxn();
        var msg = new Dictionary<string, object>
        {
            ["janus"] = "message",
            ["transaction"] = txn,
            ["body"] = body
        };
        if (jsep != null)
            msg["jsep"] = jsep;

        _logger.LogDebug("Sending fire-and-forget message to session {Session}/handle {Handle}, txn={Txn}",
            sessionId, handleId, txn);

        return await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", msg);
    }

    // ──────────────────────────────────────────────
    // Room queries (uses a temporary session to avoid event queue competition)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Queries the list of participants in a videoroom using a disposable
    /// temporary session. This avoids competing with the client's long-poll
    /// event queue on the user's main session.
    /// Returns the raw Janus response (plugindata with participants array).
    /// </summary>
    public async Task<JsonElement> ListParticipantsAsync(long roomId)
    {
        var tempSession = await CreateSessionAsync();
        try
        {
            var tempHandle = await AttachPluginAsync(tempSession);
            var result = await SendPluginMessageAndWaitAsync(
                tempSession,
                tempHandle,
                new { request = "listparticipants", room = roomId },
                requireJsep: false,
                requiredPluginDataField: "participants");

            if (TryGetPluginError(result, out var errorCode, out var errorText))
            {
                throw new InvalidOperationException(
                    $"Janus listparticipants failed for room {roomId}: " +
                    $"error_code={errorCode?.ToString() ?? "unknown"}, error={errorText ?? "unknown"}");
            }

            return result;
        }
        finally
        {
            // Best-effort cleanup — don't let it fail the request
            try { await DestroySessionAsync(tempSession); } catch { }
        }
    }

    // ──────────────────────────────────────────────
    // Trickle ICE
    // ──────────────────────────────────────────────

    public async Task TrickleAsync(long sessionId, long handleId, object candidate)
    {
        await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", new
        {
            janus = "trickle",
            candidate,
            transaction = NewTxn()
        });
    }

    public async Task TrickleCompletedAsync(long sessionId, long handleId)
    {
        // Janus expects {"completed": true} to signal end-of-candidates,
        // NOT null. Sending null causes ICE gathering to stall.
        await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", new
        {
            janus = "trickle",
            candidate = new { completed = true },
            transaction = NewTxn()
        });
    }

    // ──────────────────────────────────────────────
    // Long-poll (used by the client to receive events)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Polls Janus for one event. Returns null on timeout/error.
    /// Called by JanusController so the client can receive async events
    /// (new publishers, ICE renegotiations, etc.).
    /// </summary>
    public async Task<JsonElement?> LongPollAsync(long sessionId)
    {
        return await RawPollAsync(sessionId, timeoutSeconds: LongPollSeconds, throwOnNotFound: true);
    }

    // ──────────────────────────────────────────────
    // Keepalive (called periodically to prevent session expiry)
    // ──────────────────────────────────────────────

    public async Task SendKeepaliveAsync(long sessionId)
    {
        try
        {
            await PostAsync($"{_janusUrl}/{sessionId}", new
            {
                janus = "keepalive",
                transaction = NewTxn()
            });
        }
        catch { /* best effort */ }
    }

    // ──────────────────────────────────────────────
    // Internals
    // ──────────────────────────────────────────────

    private async Task<JsonElement?> RawPollAsync(
        long sessionId,
        int timeoutSeconds = LongPollSeconds,
        bool throwOnNotFound = false)
    {
        if (ShouldUseWebSocket)
        {
            try
            {
                return await RawPollWsAsync(sessionId, timeoutSeconds, throwOnNotFound);
            }
            catch (WebSocketException ex) when (EnableHttpFallback(ex))
            {
                // Fall back to HTTP(S) below.
            }
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds + 5);
        try
        {
            var pollUrl = ResolveHttpUrl($"{_janusUrl}/{sessionId}?maxev=1&timeout={timeoutSeconds}");
            var resp = await client.GetAsync(
                pollUrl);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                if (throwOnNotFound && (int)resp.StatusCode == 404)
                {
                    throw new JanusSessionNotFoundException(sessionId);
                }

                _logger.LogWarning(
                    "RawPollAsync HTTP {StatusCode} for session {Session}. Body: {Body}",
                    (int)resp.StatusCode,
                    sessionId,
                    string.IsNullOrWhiteSpace(body) ? "<empty>" : body);
                return null;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                _logger.LogDebug("RawPollAsync empty response body for session {Session}", sessionId);
                return null;
            }

            var doc = JsonSerializer.Deserialize<JsonElement>(body);
            return doc;
        }
        catch (TaskCanceledException)
        {
            // Timeout — normal for long-poll
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawPollAsync failed for session {Session}", sessionId);
            return null;
        }
    }

    private async Task<JsonElement> PostAsync(string url, object payload)
    {
        if (ShouldUseWebSocket)
        {
            try
            {
                return await PostWsAsync(url, payload);
            }
            catch (WebSocketException ex) when (EnableHttpFallback(ex))
            {
                // Fall back to HTTP(S) below.
            }
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var json = JsonSerializer.Serialize(payload);
        var resolvedUrl = ResolveHttpUrl(url);
        _logger.LogDebug("POST {Url}: {Json}", resolvedUrl, json);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(resolvedUrl, content);
        var body = await response.Content.ReadAsStringAsync();
        _logger.LogDebug("Response from {Url}: {Status} {Body}", resolvedUrl, response.StatusCode, body);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private bool ShouldUseWebSocket => _useWebSocket && !_forceHttpFallback;

    private bool EnableHttpFallback(Exception ex)
    {
        if (!_useWebSocket || _forceHttpFallback || string.IsNullOrWhiteSpace(_httpFallbackJanusUrl))
            return false;

        _forceHttpFallback = true;
        _logger.LogWarning(
            ex,
            "Janus WebSocket failed at {WsUrl}. Falling back to HTTP(S) at {HttpUrl}.",
            _janusUrl,
            _httpFallbackJanusUrl);
        return true;
    }

    private string ResolveHttpUrl(string url)
    {
        if (!ShouldUseWebSocket && !string.IsNullOrWhiteSpace(_httpFallbackJanusUrl))
        {
            if (url.StartsWith(_janusUrl, StringComparison.OrdinalIgnoreCase))
            {
                return _httpFallbackJanusUrl + url[_janusUrl.Length..];
            }
        }

        return url;
    }

    private async Task<JsonElement?> RawPollWsAsync(long sessionId, int timeoutSeconds, bool throwOnNotFound)
    {
        if (_wsMissingSessions.ContainsKey(sessionId))
        {
            if (throwOnNotFound)
                throw new JanusSessionNotFoundException(sessionId);
            return null;
        }

        await EnsureWsConnectedAsync();
        var queue = EnsureWsSessionQueue(sessionId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds) + 1));

        try
        {
            var ev = await queue.Reader.ReadAsync(cts.Token);

            if (IsSessionNotFoundError(ev))
            {
                _wsMissingSessions.TryAdd(sessionId, 0);
                if (throwOnNotFound)
                    throw new JanusSessionNotFoundException(sessionId);
                return null;
            }

            return ev;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<JsonElement> PostWsAsync(string url, object payload)
    {
        await EnsureWsConnectedAsync();

        var (sessionId, handleId) = ParseSessionAndHandleFromUrl(url);
        var node = JsonSerializer.SerializeToNode(payload) as JsonObject ?? new JsonObject();

        if (sessionId.HasValue && !node.ContainsKey("session_id"))
            node["session_id"] = sessionId.Value;
        if (handleId.HasValue && !node.ContainsKey("handle_id"))
            node["handle_id"] = handleId.Value;

        var txn = node["transaction"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(txn))
        {
            txn = NewTxn();
            node["transaction"] = txn;
        }

        var response = await SendWsAndAwaitTransactionAsync(txn!, node);
        if (sessionId.HasValue)
            EnsureWsSessionQueue(sessionId.Value);

        if (node["janus"]?.GetValue<string>() == "create"
            && response.TryGetProperty("data", out var dataEl)
            && dataEl.ValueKind == JsonValueKind.Object
            && dataEl.TryGetProperty("id", out var idEl)
            && idEl.ValueKind == JsonValueKind.Number
            && idEl.TryGetInt64(out var createdSessionId))
        {
            EnsureWsSessionQueue(createdSessionId);
            _wsMissingSessions.TryRemove(createdSessionId, out _);
        }

        if (node["janus"]?.GetValue<string>() == "destroy" && sessionId.HasValue)
        {
            _wsSessionEvents.TryRemove(sessionId.Value, out _);
            _wsMissingSessions.TryRemove(sessionId.Value, out _);
        }

        if (IsSessionNotFoundError(response))
        {
            var missingSession = sessionId
                                 ?? TryGetSessionId(response)
                                 ?? 0;
            if (missingSession != 0)
                _wsMissingSessions.TryAdd(missingSession, 0);
        }

        return response;
    }

    private (long? sessionId, long? handleId) ParseSessionAndHandleFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target))
            return (null, null);

        var basePath = _janusUri.AbsolutePath.TrimEnd('/');
        var targetPath = target.AbsolutePath.TrimEnd('/');

        if (!targetPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            return (null, null);

        var suffix = targetPath[basePath.Length..].Trim('/');
        if (string.IsNullOrWhiteSpace(suffix))
            return (null, null);

        var parts = suffix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        long? sessionId = null;
        long? handleId = null;

        if (parts.Length >= 1 && long.TryParse(parts[0], out var parsedSession))
            sessionId = parsedSession;
        if (parts.Length >= 2 && long.TryParse(parts[1], out var parsedHandle))
            handleId = parsedHandle;

        return (sessionId, handleId);
    }

    private Channel<JsonElement> EnsureWsSessionQueue(long sessionId)
    {
        return _wsSessionEvents.GetOrAdd(
            sessionId,
            _ => Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            }));
    }

    private async Task EnsureWsConnectedAsync()
    {
        if (!_useWebSocket)
            return;

        if (_wsClient?.State == WebSocketState.Open)
            return;

        await _wsConnectLock.WaitAsync();
        try
        {
            if (_wsClient?.State == WebSocketState.Open)
                return;

            await ResetWsConnectionAsync();

            var client = new ClientWebSocket();
            var cts = new CancellationTokenSource();
            client.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            client.Options.HttpVersion = HttpVersion.Version11;
            client.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            if (!string.IsNullOrWhiteSpace(_wsSubProtocol))
                client.Options.AddSubProtocol(_wsSubProtocol);
            if (!string.IsNullOrWhiteSpace(_wsOrigin))
                client.Options.SetRequestHeader("Origin", _wsOrigin);
            if (!string.IsNullOrWhiteSpace(_wsUserAgent))
                client.Options.SetRequestHeader("User-Agent", _wsUserAgent);
            await client.ConnectAsync(_janusUri, cts.Token);

            _wsClient = client;
            _wsCts = cts;
            _wsReceiveLoopTask = Task.Run(() => ReceiveWsLoopAsync(client, cts.Token));
            _logger.LogInformation(
                "Connected to Janus over WebSocket: {Url} (subprotocol={SubProtocol}, origin={Origin})",
                _janusUrl,
                _wsSubProtocol ?? "<none>",
                _wsOrigin ?? "<none>");
        }
        finally
        {
            _wsConnectLock.Release();
        }
    }

    private async Task ResetWsConnectionAsync()
    {
        var priorClient = _wsClient;
        var priorCts = _wsCts;
        var priorReceiveTask = _wsReceiveLoopTask;

        _wsClient = null;
        _wsCts = null;
        _wsReceiveLoopTask = null;

        if (priorCts != null)
        {
            try { priorCts.Cancel(); } catch { }
        }

        if (priorClient != null)
        {
            try
            {
                if (priorClient.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await priorClient.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "reset",
                        CancellationToken.None);
                }
            }
            catch { }
            finally
            {
                priorClient.Dispose();
            }
        }

        if (priorReceiveTask != null)
        {
            try { await priorReceiveTask; } catch { }
        }

        FailWsWaiters(new InvalidOperationException("Janus WebSocket connection reset"));
    }

    private async Task<JsonElement> SendWsAndAwaitTransactionAsync(string transaction, JsonObject message)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_wsPendingTransactions.TryAdd(transaction, tcs))
            throw new InvalidOperationException($"Duplicate Janus transaction id '{transaction}'");

        try
        {
            await SendWsAsync(message);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _wsCts?.Token ?? CancellationToken.None);
            using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));
            return await tcs.Task;
        }
        catch (TaskCanceledException)
        {
            throw new TimeoutException($"Janus did not respond to transaction {transaction} within 30s");
        }
        finally
        {
            _wsPendingTransactions.TryRemove(transaction, out _);
        }
    }

    private async Task SendWsAsync(JsonObject message)
    {
        var client = _wsClient ?? throw new InvalidOperationException("Janus WebSocket is not connected");
        var json = message.ToJsonString();
        var bytes = Encoding.UTF8.GetBytes(json);

        _logger.LogDebug("WS SEND {Json}", json);
        await _wsSendLock.WaitAsync();
        try
        {
            await client.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                _wsCts?.Token ?? CancellationToken.None);
        }
        finally
        {
            _wsSendLock.Release();
        }
    }

    private async Task ReceiveWsLoopAsync(ClientWebSocket client, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning("Janus WebSocket closed: {Status} {Description}",
                            client.CloseStatus, client.CloseStatusDescription);
                        return;
                    }

                    if (result.Count > 0)
                        ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                ms.SetLength(0);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                _logger.LogDebug("WS RECV {Json}", json);
                JsonElement response;
                try
                {
                    response = JsonSerializer.Deserialize<JsonElement>(json);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse Janus WebSocket message: {Json}", json);
                    continue;
                }

                if (response.TryGetProperty("transaction", out var txnEl)
                    && txnEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(txnEl.GetString())
                    && _wsPendingTransactions.TryRemove(txnEl.GetString()!, out var waiter))
                {
                    waiter.TrySetResult(response);
                    continue;
                }

                var sessionId = TryGetSessionId(response);
                if (sessionId.HasValue)
                {
                    EnsureWsSessionQueue(sessionId.Value).Writer.TryWrite(response);
                    continue;
                }

                _logger.LogDebug("Ignoring Janus WS message without transaction/session_id");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Janus WebSocket receive loop failed");
        }
        finally
        {
            FailWsWaiters(new InvalidOperationException("Janus WebSocket disconnected"));
            _wsClient = null;
            _wsCts = null;
            _wsReceiveLoopTask = null;
        }
    }

    private void FailWsWaiters(Exception ex)
    {
        foreach (var kv in _wsPendingTransactions.ToArray())
        {
            if (_wsPendingTransactions.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(ex);
        }
    }

    private static long? TryGetSessionId(JsonElement response)
    {
        if (response.TryGetProperty("session_id", out var sessionEl)
            && sessionEl.ValueKind == JsonValueKind.Number
            && sessionEl.TryGetInt64(out var sessionId))
        {
            return sessionId;
        }

        return null;
    }

    private static bool IsSessionNotFoundError(JsonElement response)
    {
        if (!response.TryGetProperty("janus", out var janusEl)
            || !string.Equals(janusEl.GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!response.TryGetProperty("error", out var errorEl)
            || errorEl.ValueKind != JsonValueKind.Object
            || !errorEl.TryGetProperty("code", out var codeEl)
            || codeEl.ValueKind != JsonValueKind.Number
            || !codeEl.TryGetInt32(out var code))
        {
            return false;
        }

        return code is 458 or 459;
    }

    private static string NewTxn() => Guid.NewGuid().ToString("N");

    private static bool IsMatchingPluginResponse(JsonElement response, string txn, long handleId)
    {
        if (!response.TryGetProperty("janus", out var janusTypeEl))
            return false;

        var janusType = janusTypeEl.GetString();
        // "ack" is never a final plugin response.
        if (janusType == "ack")
            return false;

        // With full_trickle enabled Janus emits non-plugin events; ignore them.
        if (janusType is "webrtcup" or "media" or "trickle" or "hangup" or "detached" or "keepalive")
            return false;

        if (response.TryGetProperty("transaction", out var txnEl) && !string.IsNullOrWhiteSpace(txnEl.GetString()))
            return txnEl.GetString() == txn;

        // Fallback for Janus events without transaction: require sender match.
        if (response.TryGetProperty("sender", out var senderEl)
            && senderEl.ValueKind == JsonValueKind.Number
            && senderEl.TryGetInt64(out var senderId))
        {
            return senderId == handleId;
        }

        return false;
    }

    private static bool IsExpectedPluginResponse(
        JsonElement response,
        bool requireJsep,
        string? requiredPluginDataField,
        out string reason)
    {
        if (HasPluginError(response))
        {
            reason = "plugin returned an error payload";
            return true;
        }

        if (requireJsep && !HasJsep(response))
        {
            reason = "missing JSEP";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requiredPluginDataField)
            && !HasPluginDataField(response, requiredPluginDataField!))
        {
            reason = $"missing plugindata.data.{requiredPluginDataField}";
            return false;
        }

        if (!response.TryGetProperty("janus", out var janusTypeEl))
        {
            reason = "missing janus field";
            return false;
        }

        if (janusTypeEl.GetString() == "ack")
        {
            reason = "ack only";
            return false;
        }

        reason = "complete response";
        return true;
    }

    private static bool HasJsep(JsonElement response)
    {
        return response.TryGetProperty("jsep", out var jsep) && jsep.ValueKind == JsonValueKind.Object;
    }

    private static bool HasPluginDataField(JsonElement response, string fieldName)
    {
        return response.TryGetProperty("plugindata", out var pluginData)
            && pluginData.ValueKind == JsonValueKind.Object
            && pluginData.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(fieldName, out _);
    }

    private static bool HasPluginError(JsonElement response)
    {
        // Janus-level error object.
        if (response.TryGetProperty("janus", out var janusType) && janusType.GetString() == "error")
            return true;

        // VideoRoom plugin error payload.
        return response.TryGetProperty("plugindata", out var pluginData)
            && pluginData.ValueKind == JsonValueKind.Object
            && pluginData.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && (data.TryGetProperty("error_code", out _) || data.TryGetProperty("error", out _));
    }

    private static bool TryGetPluginError(JsonElement response, out int? errorCode, out string? errorText)
    {
        errorCode = null;
        errorText = null;

        if (response.TryGetProperty("error", out var janusError) && janusError.ValueKind == JsonValueKind.Object)
        {
            if (janusError.TryGetProperty("code", out var codeEl)
                && codeEl.ValueKind == JsonValueKind.Number
                && codeEl.TryGetInt32(out var code))
            {
                errorCode = code;
            }
            if (janusError.TryGetProperty("reason", out var reasonEl)
                && reasonEl.ValueKind == JsonValueKind.String)
            {
                errorText = reasonEl.GetString();
            }
            return true;
        }

        if (!response.TryGetProperty("plugindata", out var pluginData)
            || pluginData.ValueKind != JsonValueKind.Object
            || !pluginData.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasError = false;
        if (data.TryGetProperty("error_code", out var pluginCodeEl)
            && pluginCodeEl.ValueKind == JsonValueKind.Number
            && pluginCodeEl.TryGetInt32(out var pluginCode))
        {
            errorCode = pluginCode;
            hasError = true;
        }
        if (data.TryGetProperty("error", out var pluginErrorEl)
            && pluginErrorEl.ValueKind == JsonValueKind.String)
        {
            errorText = pluginErrorEl.GetString();
            hasError = true;
        }

        return hasError;
    }

    public sealed class JanusSessionNotFoundException : Exception
    {
        public long SessionId { get; }

        public JanusSessionNotFoundException(long sessionId)
            : base($"Janus session {sessionId} was not found")
        {
            SessionId = sessionId;
        }
    }
}

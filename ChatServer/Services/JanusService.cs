using System.Text;
using System.Text.Json;

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
    private readonly string _janusUrl;

    // How long to wait for an event response after an ack, in total.
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(15);
    // Individual long-poll timeout sent to Janus.
    private const int LongPollSeconds = 10;

    public JanusService(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _janusUrl = config["Janus:Url"] ?? "http://localhost:8088/janus";
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
            var txn = NewTxn();
            await PostAsync($"{_janusUrl}/{sessionId}/{adminHandle}", new
            {
                janus = "message",
                transaction = txn,
                body = new
                {
                    request = "create",
                    room = roomId,
                    description = $"Room {roomId}",
                    publishers = 20,
                    bitrate = 512000,
                    videocodec = "vp8",
                    audiocodec = "opus",
                    is_private = false,
                    permanent = false
                }
            });

            // Poll for the event confirming creation (or the "already exists" error).
            var deadline = DateTime.UtcNow + EventTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var ev = await RawPollAsync(sessionId);
                if (ev == null) continue;

                if (ev.Value.TryGetProperty("janus", out var t) && t.GetString() is "event" or "error")
                {
                    // 427 = room already exists — that's fine.
                    break;
                }
            }
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

        var immediate = await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", msg);

        // If we got a direct response (not an ack), return it immediately.
        if (immediate.TryGetProperty("janus", out var j) && j.GetString() != "ack")
            return immediate;

        // Otherwise poll until we see an event for our handle.
        var deadline = DateTime.UtcNow + EventTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var ev = await RawPollAsync(sessionId);
            if (ev == null) continue;
            if (!ev.Value.TryGetProperty("janus", out var evType)) continue;

            var evTypeStr = evType.GetString();
            if (evTypeStr is "keepalive" or "ack") continue;

            // Match by sender (handleId) — Janus attaches "sender" to events.
            if (ev.Value.TryGetProperty("sender", out var sender) &&
                sender.GetInt64() == handleId)
            {
                return ev.Value;
            }

            // Also accept any event if there's only one handle (fallback).
            if (evTypeStr == "event")
                return ev.Value;
        }

        throw new TimeoutException($"Janus did not respond to transaction {txn} within {EventTimeout.TotalSeconds}s");
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
        await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", new
        {
            janus = "trickle",
            candidate = (object?)null,
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
        return await RawPollAsync(sessionId, timeoutSeconds: LongPollSeconds);
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

    private async Task<JsonElement?> RawPollAsync(long sessionId, int timeoutSeconds = LongPollSeconds)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds + 5);
        try
        {
            var resp = await client.GetAsync(
                $"{_janusUrl}/{sessionId}?maxev=1&timeout={timeoutSeconds}");
            var body = await resp.Content.ReadAsStringAsync();
            var doc = JsonSerializer.Deserialize<JsonElement>(body);
            return doc;
        }
        catch
        {
            return null;
        }
    }

    private async Task<JsonElement> PostAsync(string url, object payload)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static string NewTxn() => Guid.NewGuid().ToString("N");
}

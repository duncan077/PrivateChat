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
    private const string DesiredVideoCodecsCsv = "av1,h265,h264,vp9,vp8";
    private static readonly string[] DesiredVideoCodecOrder = ["av1", "h265", "h264", "vp9", "vp8"];
    private static readonly string[] RequiredVideoCodecs = ["av1", "h265", "h264"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JanusService> _logger;
    private readonly string _janusUrl;
    private readonly Uri _janusUri;
    private readonly bool _useWebSocket;
    private readonly string? _httpFallbackJanusUrl;
    private readonly string? _wsSubProtocol;
    private readonly string? _wsOrigin;
    private readonly string? _wsUserAgent;

    /// <summary>
    /// Credentials the videoroom plugin demands for privileged room requests, both
    /// optional. <c>secret</c> guards destroy/edit on a room that declares one - every
    /// room defined in janus.plugin.videoroom.jcfg does, including the stock demo room.
    /// <c>admin_key</c> guards create when the plugin config sets one.
    ///
    /// Unset means "this deployment configures neither", which is the case for rooms
    /// this server creates itself, so leaving them empty preserves today's behaviour.
    /// </summary>
    private readonly string? _roomSecret;
    private readonly string? _roomAdminKey;
    private volatile bool _forceHttpFallback;
    private DateTime _httpFallbackSince;
    private static readonly TimeSpan HttpFallbackCooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// One long-lived session/handle for room administration, replacing the attach and
    /// detach pair that every call join used to perform.
    /// </summary>
    private readonly SemaphoreSlim _adminLock = new(1, 1);

    /// <summary>
    /// Session and handle travel together in one immutable object so they can be swapped
    /// with a single reference assignment. As two separate long fields a reader on the
    /// lock-free fast path could see one updated and the other not - in particular
    /// EnsureWsConnectedAsync resets them without holding <see cref="_adminLock"/>, which
    /// would otherwise let a caller observe a live session id paired with handle 0 and
    /// send admin messages to a handle that was never attached.
    /// </summary>
    private sealed record AdminHandle(long SessionId, long HandleId);
    private volatile AdminHandle? _admin;

    /// <summary>
    /// Rooms whose codec policy has already been confirmed. The desired policy is a
    /// compile-time constant, so the answer cannot change between joins - but rooms are
    /// created non-permanent, so this must be dropped whenever the socket reconnects,
    /// which is the point at which Janus may have restarted and lost them.
    /// </summary>
    private readonly ConcurrentDictionary<long, byte> _codecValidatedRooms = new();

    /// <summary>
    /// Short-lived per-room participant snapshots with single-flight, so N clients
    /// polling the same call collapse to one Janus query.
    /// </summary>
    private readonly ConcurrentDictionary<long, ParticipantsEntry> _participantsCache = new();
    private static readonly TimeSpan ParticipantsCacheTtl = TimeSpan.FromSeconds(2);

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
    // Backstop pause before re-polling after a poll that may have returned immediately.
    private static readonly TimeSpan PollRetryDelay = TimeSpan.FromMilliseconds(250);

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
        _roomSecret = config["Janus:RoomSecret"];
        _roomAdminKey = config["Janus:AdminKey"];

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
        var resp = await PostAsync(_janusUrl, new JsonObject
        {
            ["janus"] = "create",
            ["transaction"] = txn
        });
        // "create" is synchronous — Janus returns the id directly (no ack).
        return ReadIdFromDataOrThrow(resp, sessionId: null, "create session");
    }

    public async Task<long> AttachPluginAsync(long sessionId, string plugin = "janus.plugin.videoroom")
    {
        var txn = NewTxn();
        var resp = await PostAsync($"{_janusUrl}/{sessionId}", new JsonObject
        {
            ["janus"] = "attach",
            ["plugin"] = plugin,
            ["transaction"] = txn
        });
        // "attach" is also synchronous.
        return ReadIdFromDataOrThrow(resp, sessionId, $"attach {plugin}");
    }

    /// <summary>
    /// Reads data.id from a synchronous Janus reply, turning a refusal into a description
    /// of what Janus actually said.
    ///
    /// Both callers assumed success and indexed straight into "data", so a Janus error -
    /// most often 458 for a session that no longer exists - surfaced as a bare
    /// KeyNotFoundException from JsonElement.GetProperty. The controller then returned it
    /// as a 502 whose body said "The given key was not present in the dictionary", which
    /// told nobody anything: the real cause of a failed attach was unreadable from either
    /// the client or the server log.
    /// </summary>
    private static long ReadIdFromDataOrThrow(JsonElement response, long? sessionId, string operation)
    {
        if (sessionId.HasValue && IsSessionNotFoundError(response))
        {
            throw new JanusSessionNotFoundException(sessionId.Value);
        }

        if (TryGetPluginError(response, out var errorCode, out var errorText))
        {
            throw new InvalidOperationException(
                $"Janus {operation} failed: error_code={errorCode?.ToString() ?? "unknown"}, "
                + $"error={errorText ?? "unknown"}");
        }

        if (!response.TryGetProperty("data", out var dataEl)
            || dataEl.ValueKind != JsonValueKind.Object
            || !dataEl.TryGetProperty("id", out var idEl)
            || !idEl.TryGetInt64(out var id))
        {
            throw new InvalidOperationException(
                $"Janus {operation} returned no id: {response.GetRawText()}");
        }

        return id;
    }

    public async Task DestroySessionAsync(long sessionId)
    {
        try
        {
            await PostAsync($"{_janusUrl}/{sessionId}", new JsonObject
            {
                ["janus"] = "destroy",
                ["transaction"] = NewTxn()
            });
        }
        catch { /* best effort */ }
    }

    public async Task DetachHandleAsync(long sessionId, long handleId)
    {
        try
        {
            await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", new JsonObject
            {
                ["janus"] = "detach",
                ["transaction"] = NewTxn()
            });
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Periodic keyframe (FIR) request interval, in seconds, enforced by Janus on
    /// every publisher in the room. With 0 (disabled) a new subscriber could wait
    /// on the publisher's next voluntary keyframe before rendering anything.
    /// Costs a bitrate spike per interval per publisher.
    /// </summary>
    private const int RoomFirFrequencySeconds = 2;

    private static int ReadRoomFirFreq(JsonElement roomEntry)
        => roomEntry.TryGetProperty("fir_freq", out var el)
           && el.ValueKind == JsonValueKind.Number
           && el.TryGetInt32(out var value)
            ? value
            : 0;

    /// <summary>
    /// Builds the videoroom "create" request. Shared by initial room creation and
    /// by the two codec-policy recreate paths so the settings cannot drift apart.
    /// </summary>
    private JsonObject BuildCreateRoomBody(
        long roomId,
        string description,
        int publishers = 20,
        long bitrate = 0,
        bool isPrivate = false)
    {
        var body = new JsonObject
        {
            ["request"] = "create",
            ["room"] = roomId,
            ["description"] = description,
            ["publishers"] = publishers,
            // 0 = no server-side cap; per-publisher bitrate is controlled by the client.
            ["bitrate"] = bitrate,
            // Keep AV1/HEVC/H264 available; the browser decides the actual offer.
            // IMPORTANT: Janus expects comma-separated codec names without spaces.
            ["videocodec"] = DesiredVideoCodecsCsv,
            ["audiocodec"] = "opus,pcmu",
            ["is_private"] = isPrivate,
            ["permanent"] = false,
            ["remb"] = false,
            ["fir_freq"] = RoomFirFrequencySeconds,
            ["rtx"] = true
        };

        // Required only when the plugin config declares an admin_key, in which case
        // create fails the same way destroy did: 429, missing mandatory element.
        if (!string.IsNullOrWhiteSpace(_roomAdminKey))
        {
            body["admin_key"] = _roomAdminKey;
        }

        return body;
    }

    /// <summary>
    /// Builds the videoroom "destroy" request, carrying the room secret when one is
    /// configured.
    ///
    /// Janus rejects destroy with error 429 "Missing mandatory element (secret)" for any
    /// room that declares one. Rooms this server creates do not, which is why call rooms
    /// were fine, but every room defined statically in janus.plugin.videoroom.jcfg does -
    /// so the startup codec sweep could never rewrite them and gave up on each one.
    /// Janus ignores the field for rooms without a secret, so sending it always is safe.
    /// </summary>
    private JsonObject BuildDestroyRoomBody(long roomId)
    {
        var body = new JsonObject
        {
            ["request"] = "destroy",
            ["room"] = roomId
        };

        if (!string.IsNullOrWhiteSpace(_roomSecret))
        {
            body["secret"] = _roomSecret;
        }

        return body;
    }

    // ──────────────────────────────────────────────
    // Video room management
    // ──────────────────────────────────────────────

    /// <summary>
    /// Ensures a videoroom exists. Uses a dedicated temporary handle so the
    /// caller's publisher handle is not polluted with admin messages.
    /// Always tries "create"; if the room already exists Janus returns error
    /// code 427; in that case we verify codec policy and recreate if needed.
    /// </summary>
    /// <summary>
    /// Runs an admin-session operation, rebuilding the session once if it turns out to be
    /// dead.
    ///
    /// The admin session is long-lived and nothing keeps it warm, so Janus reaps it after
    /// its idle timeout (60s by default) whenever the server is quiet. Detecting that via
    /// <c>_wsMissingSessions</c> alone is not enough: that dictionary is only ever written
    /// on the WebSocket path, so under HTTP fallback - or an http-scheme Janus:Url - a
    /// dead admin session would be handed out forever. Retrying once on any failure, with
    /// the cached handle dropped first, covers every transport uniformly.
    /// </summary>
    private async Task<T> WithAdminHandleAsync<T>(Func<AdminHandle, Task<T>> operation)
    {
        var admin = await GetAdminHandleAsync();

        try
        {
            return await operation(admin);
        }
        catch (Exception ex)
        {
            // Drop only if nobody has replaced it in the meantime.
            Interlocked.CompareExchange(ref _admin, null, admin);
            _logger.LogWarning(ex, "Janus admin operation failed on session {Session}; retrying once with a fresh session.", admin.SessionId);
        }

        var rebuilt = await GetAdminHandleAsync();
        return await operation(rebuilt);
    }

    public async Task EnsureVideoRoomAsync(long roomId)
    {
        // Already confirmed since the last socket connect - nothing to ask Janus.
        // This is the steady-state path for every rejoin, and it used to cost a
        // create (427), a full room list, a linear scan of it, and a detach.
        if (_codecValidatedRooms.ContainsKey(roomId))
        {
            return;
        }

        var validated = await WithAdminHandleAsync(async admin =>
        {
            var createResult = await SendPluginMessageAndWaitAsync(
                admin.SessionId,
                admin.HandleId,
                BuildCreateRoomBody(roomId, $"Room {roomId}"));

            if (TryGetPluginError(createResult, out var createErrorCode, out var createErrorText))
            {
                // 427 = room already exists; this is the expected no-op path.
                if (createErrorCode == 427)
                {
                    _logger.LogDebug("Videoroom {Room} already exists; validating codec policy", roomId);
                    return await EnsureExistingRoomCodecPolicyAsync(admin.SessionId, admin.HandleId, roomId);
                }

                throw new InvalidOperationException(
                    $"Janus create room failed for room {roomId}: " +
                    $"error_code={createErrorCode?.ToString() ?? "unknown"}, error={createErrorText ?? "unknown"}");
            }

            _logger.LogDebug("Videoroom {Room} ensured", roomId);
            return true;
        });

        // Only skip the check next time if the room was genuinely confirmed. The policy
        // routine has bail-out paths - list failed, room absent from the list - that
        // validate nothing, and caching those would make the miss permanent.
        if (validated)
        {
            _codecValidatedRooms[roomId] = 1;
        }
    }

    /// <summary>
    /// The shared admin session and handle, created on first use and reused afterwards.
    /// Validated on every call so a session Janus has reaped is rebuilt rather than used.
    /// </summary>
    private async Task<AdminHandle> GetAdminHandleAsync()
    {
        var current = _admin;
        if (IsUsable(current))
        {
            return current!;
        }

        await _adminLock.WaitAsync();
        try
        {
            current = _admin;
            if (IsUsable(current))
            {
                return current!;
            }

            var superseded = current;

            // Publish only once both halves exist.
            var sessionId = await CreateSessionAsync();
            var handleId = await AttachPluginAsync(sessionId);
            var established = new AdminHandle(sessionId, handleId);
            _admin = established;

            _logger.LogDebug(
                "Janus admin session {Session}/handle {Handle} established", sessionId, handleId);

            // Retire the session this one replaces. Its event channel is never read, so
            // leaving it behind would leak an unbounded channel per rebuild.
            if (superseded != null)
            {
                ForgetSession(superseded.SessionId);
            }

            return established;
        }
        finally
        {
            _adminLock.Release();
        }

        bool IsUsable(AdminHandle? handle)
            => handle != null && !IsSessionKnownMissing(handle.SessionId);
    }

    internal bool IsSessionKnownMissing(long sessionId) => _wsMissingSessions.ContainsKey(sessionId);

    /// <summary>
    /// Drops all server-side state for a session and asks Janus to destroy it. Detached
    /// from the caller because it runs on cleanup paths where a slow or dead gateway must
    /// not hold anything up.
    /// </summary>
    private void ForgetSession(long sessionId)
    {
        _wsSessionEvents.TryRemove(sessionId, out var channel);
        channel?.Writer.TryComplete();
        _wsMissingSessions.TryRemove(sessionId, out _);

        _ = Task.Run(async () =>
        {
            try
            {
                await DestroySessionAsync(sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to destroy superseded Janus session {Session}", sessionId);
            }
        });
    }

    /// <summary>
    /// Recreates all existing Janus videorooms with the desired codec policy.
    /// Intended for startup synchronization when codec policy changes.
    /// </summary>
    public async Task<int> RecreateAllVideoRoomsAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = await CreateSessionAsync();
        long adminHandle = 0;
        var recreatedCount = 0;

        try
        {
            adminHandle = await AttachPluginAsync(sessionId);
            var listResult = await SendPluginMessageAndWaitAsync(
                sessionId,
                adminHandle,
                new JsonObject { ["request"] = "list" },
                requireJsep: false,
                requiredPluginDataField: "list");

            if (TryGetPluginError(listResult, out var listErrorCode, out var listErrorText))
            {
                throw new InvalidOperationException(
                    $"Janus room list failed: error_code={listErrorCode?.ToString() ?? "unknown"}, error={listErrorText ?? "unknown"}");
            }

            if (!TryGetRoomListEntries(listResult, out var rooms))
            {
                _logger.LogWarning("Startup codec sync skipped: Janus 'list' response had no room array");
                return 0;
            }

            foreach (var roomEntry in rooms)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryReadRoomId(roomEntry, out var roomId))
                    continue;

                try
                {
                    // Two guards that were missing entirely. Without them this destroyed
                    // and recreated EVERY room on EVERY restart - including rooms with a
                    // call in progress, which dropped those calls - even though the codec
                    // policy is a compile-time constant that is usually already correct.
                    // On a steady-state restart the whole routine is now a single "list".
                    var existingCodecs = roomEntry.TryGetProperty("videocodec", out var existingCodecsEl)
                                         && existingCodecsEl.ValueKind == JsonValueKind.String
                        ? (existingCodecsEl.GetString() ?? string.Empty)
                        : string.Empty;

                    // fir_freq participates in the drift check for the same reason the
                    // codec policy does: rooms created before the value changed would
                    // otherwise keep the old interval forever.
                    var existingFirFreq = ReadRoomFirFreq(roomEntry);

                    if (IsRoomCodecPolicyCompliant(existingCodecs, out _)
                        && existingFirFreq == RoomFirFrequencySeconds)
                    {
                        _logger.LogDebug(
                            "Startup codec sync: videoroom {Room} already compliant ({Codecs}, fir_freq={FirFreq})",
                            roomId, existingCodecs, existingFirFreq);
                        continue;
                    }

                    var activeParticipants = roomEntry.TryGetProperty("num_participants", out var activeEl)
                                             && activeEl.ValueKind == JsonValueKind.Number
                                             && activeEl.TryGetInt32(out var parsedActive)
                        ? parsedActive
                        : 0;

                    if (activeParticipants > 0)
                    {
                        _logger.LogWarning(
                            "Startup codec sync: videoroom {Room} needs recreation but has {Participants} active "
                            + "participant(s); leaving it alone.",
                            roomId,
                            activeParticipants);
                        continue;
                    }

                    // The list snapshot above can be seconds to minutes old by the time
                    // this room's turn comes, so occupancy is re-checked immediately
                    // before destroying. Otherwise a room that acquired a call during the
                    // sweep gets torn down under the people in it.
                    if (await HasLiveParticipantsAsync(sessionId, adminHandle, roomId))
                    {
                        _logger.LogWarning(
                            "Startup codec sync: videoroom {Room} acquired participants during the sweep; skipping.",
                            roomId);
                        continue;
                    }

                    var roomDescription = roomEntry.TryGetProperty("description", out var descriptionEl)
                                          && descriptionEl.ValueKind == JsonValueKind.String
                        ? (descriptionEl.GetString() ?? $"Room {roomId}")
                        : $"Room {roomId}";

                    var publishers = roomEntry.TryGetProperty("max_publishers", out var publishersEl)
                                     && publishersEl.ValueKind == JsonValueKind.Number
                                     && publishersEl.TryGetInt32(out var parsedPublishers)
                        ? Math.Max(1, parsedPublishers)
                        : 20;

                    var bitrate = roomEntry.TryGetProperty("bitrate", out var bitrateEl)
                                  && bitrateEl.ValueKind == JsonValueKind.Number
                                  && bitrateEl.TryGetInt32(out var parsedBitrate)
                        ? Math.Max(0, parsedBitrate)
                        : 0;

                    var isPrivate = roomEntry.TryGetProperty("is_private", out var privateEl)
                                    && (privateEl.ValueKind == JsonValueKind.True || privateEl.ValueKind == JsonValueKind.False)
                        ? privateEl.GetBoolean()
                        : false;

                    _logger.LogWarning(
                        "Startup codec sync: recreating videoroom {Room} ({Description}) with codec policy {Codecs}",
                        roomId,
                        roomDescription,
                        DesiredVideoCodecsCsv);

                    var destroyResult = await SendPluginMessageAndWaitAsync(
                        sessionId,
                        adminHandle,
                        BuildDestroyRoomBody(roomId));

                    if (TryGetPluginError(destroyResult, out var destroyErrorCode, out var destroyErrorText))
                    {
                        // 426 = no such room (race); continue to create.
                        if (destroyErrorCode != 426)
                        {
                            _logger.LogWarning(
                                "Startup codec sync: destroy failed for room {Room} (error_code={Code}, error={Error})",
                                roomId,
                                destroyErrorCode?.ToString() ?? "unknown",
                                destroyErrorText ?? "unknown");
                            continue;
                        }
                    }

                    var createResult = await SendPluginMessageAndWaitAsync(
                        sessionId,
                        adminHandle,
                        BuildCreateRoomBody(roomId, roomDescription, publishers, bitrate, isPrivate));

                    if (TryGetPluginError(createResult, out var createErrorCode, out var createErrorText))
                    {
                        _logger.LogWarning(
                            "Startup codec sync: create failed for room {Room} (error_code={Code}, error={Error})",
                            roomId,
                            createErrorCode?.ToString() ?? "unknown",
                            createErrorText ?? "unknown");
                        continue;
                    }

                    recreatedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Startup codec sync: unexpected failure recreating room {Room}", roomId);
                }
            }

            return recreatedCount;
        }
        finally
        {
            if (adminHandle != 0)
            {
                try { await DetachHandleAsync(sessionId, adminHandle); } catch { }
            }

            try { await DestroySessionAsync(sessionId); } catch { }
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
        JsonNode body,
        JsonNode? jsep = null,
        bool requireJsep = false,
        string? requiredPluginDataField = null)
    {
        var txn = NewTxn();
        var msg = new JsonObject
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
            // throwOnNotFound matters: for a session Janus has already reaped,
            // RawPollWsAsync returns null *synchronously*, before it awaits anything.
            // Without this the loop below became a tight ContainsKey/UtcNow spin that
            // pinned a thread-pool thread flat out for the full EventTimeout - and the
            // event being waited for could never arrive anyway.
            var ev = await RawPollAsync(sessionId, throwOnNotFound: true);
            if (ev == null)
            {
                // Any other null is a poll that may have cost nothing (empty body,
                // non-success status). Yield before retrying so a misbehaving
                // transport cannot busy-wait either. A real long-poll timeout has
                // already blocked for LongPollSeconds, so this is noise against it.
                await Task.Delay(PollRetryDelay);
                continue;
            }

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
        JsonNode body,
        JsonNode? jsep = null)
    {
        var txn = NewTxn();
        var msg = new JsonObject
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
    public Task<JsonElement> ListParticipantsAsync(long roomId)
    {
        // Clients poll this every 5s each, so a ten-person call produced 120 requests a
        // minute for one room - each of which used to build and tear down a whole Janus
        // session (4 round trips). Now concurrent callers within the TTL share one query
        // against the long-lived admin handle.
        var now = DateTime.UtcNow;

        var entry = _participantsCache.AddOrUpdate(
            roomId,
            _ => new ParticipantsEntry(now, roomId, this),
            (_, existing) => IsUsable(existing, now) ? existing : new ParticipantsEntry(now, roomId, this));

        return entry.Query.Value;

        static bool IsUsable(ParticipantsEntry entry, DateTime now)
        {
            if (now - entry.CreatedAt >= ParticipantsCacheTtl)
            {
                return false;
            }

            // Never serve a failure for the rest of the window.
            return !entry.Query.IsValueCreated || !entry.Query.Value.IsFaulted;
        }
    }

    /// <summary>
    /// One in-flight participants query. The <see cref="Lazy{T}"/> is what makes this
    /// single-flight: <c>AddOrUpdate</c> may build several candidates under contention,
    /// but only the one that wins the dictionary is ever evaluated.
    /// </summary>
    private sealed class ParticipantsEntry
    {
        public ParticipantsEntry(DateTime createdAt, long roomId, JanusService owner)
        {
            CreatedAt = createdAt;
            Query = new Lazy<Task<JsonElement>>(
                () => owner.FetchParticipantsAsync(roomId),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public DateTime CreatedAt { get; }
        public Lazy<Task<JsonElement>> Query { get; }
    }

    private Task<JsonElement> FetchParticipantsAsync(long roomId)
        => WithAdminHandleAsync(async admin =>
        {
            var result = await SendPluginMessageAndWaitAsync(
                admin.SessionId,
                admin.HandleId,
                new JsonObject { ["request"] = "listparticipants", ["room"] = roomId },
                requireJsep: false,
                requiredPluginDataField: "participants");

            if (TryGetPluginError(result, out var errorCode, out var errorText))
            {
                throw new InvalidOperationException(
                    $"Janus listparticipants failed for room {roomId}: " +
                    $"error_code={errorCode?.ToString() ?? "unknown"}, error={errorText ?? "unknown"}");
            }

            return result;
        });

    // ──────────────────────────────────────────────
    // Trickle ICE
    // ──────────────────────────────────────────────

    public async Task TrickleAsync(long sessionId, long handleId, JsonNode candidate)
    {
        await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", new JsonObject
        {
            ["janus"] = "trickle",
            ["candidate"] = candidate,
            ["transaction"] = NewTxn()
        });
    }

    /// <summary>
    /// Forwards several candidates in one Janus message. Janus accepts a "candidates" array as
    /// the batch form of trickle, so a client that buffered a gathering burst costs one
    /// round trip here instead of one per candidate.
    /// </summary>
    public async Task TrickleManyAsync(long sessionId, long handleId, JsonArray candidates)
    {
        await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", new JsonObject
        {
            ["janus"] = "trickle",
            ["candidates"] = candidates,
            ["transaction"] = NewTxn()
        });
    }

    public async Task TrickleCompletedAsync(long sessionId, long handleId)
    {
        // Janus expects {"completed": true} to signal end-of-candidates,
        // NOT null. Sending null causes ICE gathering to stall.
        await PostAsync($"{_janusUrl}/{sessionId}/{handleId}", new JsonObject
        {
            ["janus"] = "trickle",
            ["candidate"] = new JsonObject { ["completed"] = true },
            ["transaction"] = NewTxn()
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

    /// <summary>
    /// Brings an existing room's codec policy up to date.
    /// Returns true only when the room was genuinely confirmed - the bail-out paths below
    /// validate nothing, and the caller must not cache them as validated.
    /// </summary>
    private async Task<bool> EnsureExistingRoomCodecPolicyAsync(long sessionId, long adminHandle, long roomId)
    {
        var listResult = await SendPluginMessageAndWaitAsync(
            sessionId,
            adminHandle,
            new JsonObject { ["request"] = "list" },
            requireJsep: false,
            requiredPluginDataField: "list");

        if (TryGetPluginError(listResult, out var listErrorCode, out var listErrorText))
        {
            _logger.LogWarning(
                "Unable to inspect videoroom {Room} codec policy (error_code={Code}, error={Error})",
                roomId,
                listErrorCode?.ToString() ?? "unknown",
                listErrorText ?? "unknown");
            return false;
        }

        if (!TryGetRoomListEntry(listResult, roomId, out var roomEntry))
        {
            _logger.LogWarning("Videoroom {Room} exists but was not returned by list; skipping codec policy update", roomId);
            return false;
        }

        var codecsCsv = roomEntry.TryGetProperty("videocodec", out var codecsEl) && codecsEl.ValueKind == JsonValueKind.String
            ? (codecsEl.GetString() ?? string.Empty)
            : string.Empty;

        var codecPolicyCompliant = IsRoomCodecPolicyCompliant(codecsCsv, out var policyReason);
        var firFreq = ReadRoomFirFreq(roomEntry);
        if (firFreq != RoomFirFrequencySeconds)
        {
            var firReason = $"fir_freq {firFreq} != {RoomFirFrequencySeconds}";
            policyReason = string.IsNullOrWhiteSpace(policyReason) || codecPolicyCompliant
                ? firReason
                : $"{policyReason}; {firReason}";
        }

        if (codecPolicyCompliant && firFreq == RoomFirFrequencySeconds)
        {
            _logger.LogDebug(
                "Videoroom {Room} settings are compliant: {Codecs}, fir_freq={FirFreq}", roomId, codecsCsv, firFreq);
            return true;
        }

        var participantCount = roomEntry.TryGetProperty("num_participants", out var participantsEl)
                               && participantsEl.ValueKind == JsonValueKind.Number
                               && participantsEl.TryGetInt32(out var parsedParticipants)
            ? parsedParticipants
            : 0;

        if (participantCount > 0)
        {
            _logger.LogWarning(
                "Videoroom {Room} codec policy is outdated ({Existing}; reason={Reason}); cannot recreate while {Participants} participant(s) are active",
                roomId,
                string.IsNullOrWhiteSpace(codecsCsv) ? "<none>" : codecsCsv,
                policyReason,
                participantCount);
            // Left non-compliant on purpose; do not record it as validated.
            return false;
        }

        _logger.LogWarning(
            "Videoroom {Room} codec policy is outdated ({Existing}; reason={Reason}); recreating with {Desired}",
            roomId,
            string.IsNullOrWhiteSpace(codecsCsv) ? "<none>" : codecsCsv,
            policyReason,
            DesiredVideoCodecsCsv);

        var destroyResult = await SendPluginMessageAndWaitAsync(
            sessionId,
            adminHandle,
            BuildDestroyRoomBody(roomId));

        if (TryGetPluginError(destroyResult, out var destroyErrorCode, out var destroyErrorText))
        {
            // 426 = no such room (race); safe to continue to create.
            if (destroyErrorCode != 426)
            {
                throw new InvalidOperationException(
                    $"Janus destroy room failed for room {roomId}: " +
                    $"error_code={destroyErrorCode?.ToString() ?? "unknown"}, error={destroyErrorText ?? "unknown"}");
            }
        }

        var recreateResult = await SendPluginMessageAndWaitAsync(
            sessionId,
            adminHandle,
            BuildCreateRoomBody(roomId, $"Room {roomId}"));

        if (TryGetPluginError(recreateResult, out var recreateErrorCode, out var recreateErrorText))
        {
            throw new InvalidOperationException(
                $"Janus recreate room failed for room {roomId}: " +
                $"error_code={recreateErrorCode?.ToString() ?? "unknown"}, error={recreateErrorText ?? "unknown"}");
        }

        _logger.LogInformation("Videoroom {Room} recreated with codec policy {Codecs}", roomId, DesiredVideoCodecsCsv);
        return true;
    }

    private static List<string> ParseCodecCsv(string codecsCsv)
    {
        if (string.IsNullOrWhiteSpace(codecsCsv))
        {
            return [];
        }

        return codecsCsv
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static c => c.Trim().ToLowerInvariant())
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .ToList();
    }

    private static bool IsRoomCodecPolicyCompliant(string codecsCsv, out string reason)
    {
        var actualCodecs = ParseCodecCsv(codecsCsv);
        if (actualCodecs.Count == 0)
        {
            reason = "empty-codec-policy";
            return false;
        }

        var codecSet = actualCodecs.ToHashSet(StringComparer.Ordinal);
        foreach (var requiredCodec in RequiredVideoCodecs)
        {
            if (!codecSet.Contains(requiredCodec))
            {
                reason = $"missing-required-{requiredCodec}";
                return false;
            }
        }

        var av1Index = actualCodecs.IndexOf("av1");
        var h265Index = actualCodecs.IndexOf("h265");
        var h264Index = actualCodecs.IndexOf("h264");
        if (av1Index < 0 || h265Index < 0 || h264Index < 0 || !(av1Index < h265Index && h265Index < h264Index))
        {
            reason = "primary-codec-order-mismatch";
            return false;
        }

        if (actualCodecs[0] != "av1")
        {
            reason = "default-codec-is-not-av1";
            return false;
        }

        // Prefer full match, but treat it as compliant when Janus omits optional
        // codecs while preserving AV1/H265/H264 requirements and ordering.
        var exactMatch = actualCodecs.SequenceEqual(DesiredVideoCodecOrder, StringComparer.Ordinal);
        reason = exactMatch ? "exact-match" : "ordered-required-match";
        return true;
    }

    private static bool TryGetRoomListEntry(JsonElement response, long roomId, out JsonElement roomEntry)
    {
        roomEntry = default;

        if (!TryGetRoomListEntries(response, out var list))
        {
            return false;
        }

        foreach (var candidate in list)
        {
            if (candidate.ValueKind != JsonValueKind.Object) continue;
            if (!candidate.TryGetProperty("room", out var roomEl)) continue;

            if (roomEl.ValueKind == JsonValueKind.Number && roomEl.TryGetInt64(out var candidateRoomId) && candidateRoomId == roomId)
            {
                roomEntry = candidate;
                return true;
            }

            if (roomEl.ValueKind == JsonValueKind.String
                && long.TryParse(roomEl.GetString(), out var candidateRoomIdFromString)
                && candidateRoomIdFromString == roomId)
            {
                roomEntry = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fresh occupancy check for one room. Returns false if the question cannot be
    /// answered - the caller uses this to decide whether destroying is safe, and an
    /// unanswerable check is not grounds for tearing a room down.
    /// </summary>
    private async Task<bool> HasLiveParticipantsAsync(long sessionId, long adminHandle, long roomId)
    {
        try
        {
            var result = await SendPluginMessageAndWaitAsync(
                sessionId,
                adminHandle,
                new JsonObject { ["request"] = "listparticipants", ["room"] = roomId },
                requireJsep: false,
                requiredPluginDataField: "participants");

            if (TryGetPluginError(result, out _, out _))
            {
                return false;
            }

            return result.TryGetProperty("plugindata", out var plugindata)
                   && plugindata.ValueKind == JsonValueKind.Object
                   && plugindata.TryGetProperty("data", out var data)
                   && data.ValueKind == JsonValueKind.Object
                   && data.TryGetProperty("participants", out var participants)
                   && participants.ValueKind == JsonValueKind.Array
                   && participants.GetArrayLength() > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup codec sync: occupancy re-check failed for room {Room}", roomId);
            return false;
        }
    }

    private static bool TryGetRoomListEntries(JsonElement response, out JsonElement.ArrayEnumerator list)
    {
        list = default;

        if (!response.TryGetProperty("plugindata", out var plugindata)
            || plugindata.ValueKind != JsonValueKind.Object
            || !plugindata.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("list", out var listEl)
            || listEl.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        list = listEl.EnumerateArray();
        return true;
    }

    private static bool TryReadRoomId(JsonElement roomEntry, out long roomId)
    {
        roomId = 0;
        if (!roomEntry.TryGetProperty("room", out var roomEl))
            return false;

        if (roomEl.ValueKind == JsonValueKind.Number && roomEl.TryGetInt64(out var numericRoomId))
        {
            roomId = numericRoomId;
            return true;
        }

        if (roomEl.ValueKind == JsonValueKind.String
            && long.TryParse(roomEl.GetString(), out var parsedRoomId))
        {
            roomId = parsedRoomId;
            return true;
        }

        return false;
    }

    // ──────────────────────────────────────────────
    // Keepalive (called periodically to prevent session expiry)
    // ──────────────────────────────────────────────

    public async Task SendKeepaliveAsync(long sessionId)
    {
        // Deliberately not best-effort any more. Swallowing every failure and reporting
        // success made two things impossible: the client's own keepalive-failure counter,
        // which is what escalates to a session-lost reconnect, could never trip; and the
        // endpoint was useless as a liveness probe, because it answered 200 for a session
        // Janus had already dropped.
        var response = await PostAsync($"{_janusUrl}/{sessionId}", new JsonObject
        {
            ["janus"] = "keepalive",
            ["transaction"] = NewTxn()
        });

        if (IsSessionNotFoundError(response))
        {
            throw new JanusSessionNotFoundException(sessionId);
        }
    }

    // ──────────────────────────────────────────────
    // Internals
    // ──────────────────────────────────────────────

    private async Task<JsonElement?> RawPollAsync(
        long sessionId,
        int timeoutSeconds = LongPollSeconds,
        bool throwOnNotFound = false)
    {
        // Decided once for this whole operation - see ShouldUseWebSocket.
        var useWebSocket = ShouldUseWebSocket;
        if (useWebSocket)
        {
            try
            {
                return await RawPollWsAsync(sessionId, timeoutSeconds, throwOnNotFound);
            }
            catch (WebSocketException ex) when (EnableHttpFallback(ex))
            {
                // Fall back to HTTP(S) below.
                useWebSocket = false;
            }
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds + 5);
        try
        {
            var pollUrl = ResolveHttpUrl($"{_janusUrl}/{sessionId}?maxev=1&timeout={timeoutSeconds}", useWebSocket);
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

            // Clone detaches the element from the JsonDocument being disposed.
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (TaskCanceledException)
        {
            // Timeout — normal for long-poll
            return null;
        }
        catch (JanusSessionNotFoundException)
        {
            // Thrown deliberately just above; the catch-all below would swallow it and
            // silently break the throwOnNotFound contract on the HTTP transport - which
            // is what lets the plugin-message wait loop spin instead of giving up.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawPollAsync failed for session {Session}", sessionId);
            return null;
        }
    }

    private async Task<JsonElement> PostAsync(string url, JsonObject payload)
    {
        var useWebSocket = ShouldUseWebSocket;
        if (useWebSocket)
        {
            try
            {
                return await PostWsAsync(url, payload);
            }
            catch (WebSocketException ex) when (EnableHttpFallback(ex))
            {
                // Fall back to HTTP(S) below.
                useWebSocket = false;
            }
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var json = payload.ToJsonString();
        var resolvedUrl = ResolveHttpUrl(url, useWebSocket);
        _logger.LogDebug("POST {Url}: {Json}", resolvedUrl, json);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(resolvedUrl, content);
        var body = await response.Content.ReadAsStringAsync();
        _logger.LogDebug("Response from {Url}: {Status} {Body}", resolvedUrl, response.StatusCode, body);
        response.EnsureSuccessStatusCode();
        using var responseDoc = JsonDocument.Parse(body);
        return responseDoc.RootElement.Clone();
    }

    /// <summary>
    /// The fallback is a cooldown, not a one-way door. It used to be latched for the
    /// process lifetime, so a single WebSocketException - a Janus restart, a proxy blip -
    /// downgraded every user to HTTP long-polling until the server was restarted, holding
    /// one outbound request open per participant instead of sharing one socket.
    /// </summary>
    /// <summary>
    /// Side-effect free on purpose. It is read more than once per operation - PostAsync or
    /// RawPollAsync picks the transport, then ResolveHttpUrl asks again - so a getter that
    /// mutated state could let a single request post over one transport and address itself
    /// with the other's URL. Each operation now reads it once and passes the answer down.
    /// </summary>
    private bool ShouldUseWebSocket
        => _useWebSocket
           && (!_forceHttpFallback || DateTime.UtcNow - _httpFallbackSince >= HttpFallbackCooldown);

    private bool EnableHttpFallback(Exception ex)
    {
        if (!_useWebSocket || string.IsNullOrWhiteSpace(_httpFallbackJanusUrl))
            return false;

        var alreadyFallenBack = _forceHttpFallback;
        _httpFallbackSince = DateTime.UtcNow;
        _forceHttpFallback = true;

        if (!alreadyFallenBack)
        {
            _logger.LogWarning(
                ex,
                "Janus WebSocket failed at {WsUrl}. Falling back to HTTP(S) at {HttpUrl} for {Cooldown}.",
                _janusUrl,
                _httpFallbackJanusUrl,
                HttpFallbackCooldown);
        }

        return true;
    }

    private string ResolveHttpUrl(string url, bool useWebSocket)
    {
        if (!useWebSocket && !string.IsNullOrWhiteSpace(_httpFallbackJanusUrl))
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

    private async Task<JsonElement> PostWsAsync(string url, JsonObject payload)
    {
        await EnsureWsConnectedAsync();

        var (sessionId, handleId) = ParseSessionAndHandleFromUrl(url);
        // Work on a copy: the WebSocket transport adds session_id/handle_id inline,
        // and PostAsync may still fall back to HTTP with the original payload
        // (where those ids belong in the URL instead).
        var node = payload.DeepClone().AsObject();

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

            // A reconnect is the moment Janus may have restarted. Rooms are created
            // non-permanent, so anything remembered about them is now a guess: drop the
            // codec-validation cache and the admin session so both are rebuilt on demand.
            // Skipping this would let joins proceed against rooms that no longer exist.
            _codecValidatedRooms.Clear();
            _admin = null;

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
                    using var responseDoc = JsonDocument.Parse(json);
                    response = responseDoc.RootElement.Clone();
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

using System.Text.Json;
using System.Text.Json.Nodes;
using ChatServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatServer.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class JanusController : ControllerBase
{
    private readonly JanusService _janus;
    private readonly ILogger<JanusController> _logger;

    public JanusController(JanusService janus, ILogger<JanusController> logger)
    {
        _janus = janus;
        _logger = logger;
    }

    /// <summary>
    /// Creates a dedicated Janus session.
    /// </summary>
    [HttpPost("session/create")]
    public async Task<IActionResult> CreateSession()
    {
        try
        {
            var sessionId = await _janus.CreateSessionAsync();
            return Ok(new { sessionId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed creating Janus session.");
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Destroys a Janus session.
    /// </summary>
    [HttpDelete("session/{sessionId}")]
    public async Task<IActionResult> DestroySession(long sessionId)
    {
        try
        {
            await _janus.DestroySessionAsync(sessionId);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed destroying Janus session {SessionId}.", sessionId);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Attaches a new plugin handle to an existing session.
    /// The client calls this to get a dedicated handle per subscriber feed.
    /// </summary>
    [HttpPost("session/{sessionId}/attach")]
    public async Task<IActionResult> AttachHandle(long sessionId)
    {
        try
        {
            var handleId = await _janus.AttachPluginAsync(sessionId);
            return Ok(new { handleId });
        }
        catch (Exception ex)
        {
            // Logged, not just returned. The reason only travelled in the response body,
            // and the client cannot read it: a subscriber attach failing is exactly the
            // point at which a call starts falling apart, and the server log said nothing
            // at all about it.
            _logger.LogError(ex, "Failed attaching Janus plugin handle on session {SessionId}.", sessionId);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Forwards a plugin message to Janus and waits for the real event response
    /// (not just the ack). Returns plugindata + jsep when present.
    /// </summary>
    [HttpPost("session/{sessionId}/handle/{handleId}/message")]
    public async Task<IActionResult> SendMessage(
        long sessionId,
        long handleId,
        [FromBody] JsonElement message)
    {
        try
        {
            if (!message.TryGetProperty("body", out var bodyEl))
                return BadRequest("Missing 'body' field");

            JsonElement? jsepEl = message.TryGetProperty("jsep", out var j) ? j : null;
            var requireJsep = jsepEl.HasValue;

            var result = await _janus.SendPluginMessageAndWaitAsync(
                sessionId,
                handleId,
                ToJsonNode(bodyEl)!,
                jsepEl.HasValue ? ToJsonNode(jsepEl.Value) : null,
                requireJsep: requireJsep);

            return Content(result.GetRawText(), "application/json");
        }
        catch (JanusService.JanusSessionNotFoundException ex)
        {
            // Reported promptly now rather than after a 15s wait - the plugin-message
            // wait loop gives up as soon as it learns the session is gone.
            _logger.LogWarning("Janus session {Session} not found", ex.SessionId);
            return NotFound(new { error = $"Janus session {ex.SessionId} not found (458)" });
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning("Janus timeout for session {Session}/handle {Handle}: {Error}",
                sessionId, handleId, ex.Message);
            return StatusCode(504, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Janus error for session {Session}/handle {Handle}",
                sessionId, handleId);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Sends a plugin message and returns immediately (just the Janus ACK).
    /// Does NOT poll for the async event response — the caller receives it
    /// via the long-poll endpoint instead. This prevents event-queue
    /// competition when subscribing to remote feeds.
    /// </summary>
    [HttpPost("session/{sessionId}/handle/{handleId}/message-nowait")]
    public async Task<IActionResult> SendMessageNoWait(
        long sessionId,
        long handleId,
        [FromBody] JsonElement message)
    {
        try
        {
            if (!message.TryGetProperty("body", out var bodyEl))
                return BadRequest("Missing 'body' field");

            JsonElement? jsepEl = message.TryGetProperty("jsep", out var j) ? j : null;

            var ack = await _janus.SendPluginMessageNoWaitAsync(
                sessionId,
                handleId,
                ToJsonNode(bodyEl)!,
                jsepEl.HasValue ? ToJsonNode(jsepEl.Value) : null);

            return Content(ack.GetRawText(), "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Janus fire-and-forget error for session {Session}/handle {Handle}",
                sessionId, handleId);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Forwards trickle ICE candidates to Janus. Accepts a single candidate, an array of
    /// candidates (the client batches a gathering burst into one request), or null for
    /// end-of-candidates.
    /// </summary>
    [HttpPost("session/{sessionId}/handle/{handleId}/trickle")]
    public async Task<IActionResult> Trickle(
        long sessionId,
        long handleId,
        [FromBody] JsonElement candidate)
    {
        try
        {
            switch (candidate.ValueKind)
            {
                case JsonValueKind.Null or JsonValueKind.Undefined:
                    await _janus.TrickleCompletedAsync(sessionId, handleId);
                    break;

                case JsonValueKind.Array:
                    var batch = new JsonArray();
                    var sawCompleted = false;
                    foreach (var element in candidate.EnumerateArray())
                    {
                        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        {
                            sawCompleted = true;
                            continue;
                        }

                        batch.Add(ToJsonNode(element));
                    }

                    if (batch.Count > 0)
                    {
                        await _janus.TrickleManyAsync(sessionId, handleId, batch);
                    }

                    // A null inside the batch is end-of-candidates; it must follow the
                    // candidates it was gathered with, never precede them.
                    if (sawCompleted)
                    {
                        await _janus.TrickleCompletedAsync(sessionId, handleId);
                    }
                    break;

                default:
                    await _janus.TrickleAsync(sessionId, handleId, ToJsonNode(candidate)!);
                    break;
            }

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Janus trickle error for session {Session}/handle {Handle}", sessionId, handleId);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Long-polls Janus for one event. The client calls this in a loop to
    /// receive async events (publishers joining/leaving, ICE renegotiation).
    /// Returns the raw Janus JSON directly to avoid any re-serialization issues
    /// with JsonElement → ASP.NET JSON → client.
    /// </summary>
    [HttpGet("session/{sessionId}/poll")]
    public async Task<IActionResult> LongPoll(long sessionId)
    {
        try
        {
            var result = await _janus.LongPollAsync(sessionId);
            if (result == null)
                return Ok(new { janus = "keepalive" });

            // Pass through the raw Janus JSON without re-serialization
            var rawJson = result.Value.GetRawText();

            // Debug, and guarded: this fires on every Janus event for every participant
            // and the payload is SDP/ICE. Building the truncated string unconditionally
            // allocated on a per-event path, and at Information it dumped signalling
            // detail into the production log.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Poll session {Session}: {Event}", sessionId,
                    rawJson.Length > 300 ? rawJson[..300] + "..." : rawJson);
            }

            return Content(rawJson, "application/json");
        }
        catch (JanusService.JanusSessionNotFoundException ex)
        {
            _logger.LogWarning("Poll session {Session} not found in Janus", ex.SessionId);
            return NotFound(new { error = $"Janus session {ex.SessionId} not found (458)" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Poll error for session {Session}", sessionId);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Sends a keepalive to Janus to prevent the session from expiring (60s default).
    /// The client calls this every 30s.
    /// </summary>
    [HttpPost("session/{sessionId}/keepalive")]
    public async Task<IActionResult> Keepalive(long sessionId)
    {
        try
        {
            await _janus.SendKeepaliveAsync(sessionId);
            return Ok();
        }
        catch (JanusService.JanusSessionNotFoundException ex)
        {
            _logger.LogWarning("Keepalive session {Session} not found in Janus", ex.SessionId);
            return NotFound(new { error = $"Janus session {ex.SessionId} not found (458)" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Keepalive error for session {Session}", sessionId);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Lists participants in a videoroom. Uses a temporary Janus session
    /// to avoid competing with the client's long-poll event queue.
    /// This is the fallback publisher-discovery mechanism: the client calls
    /// this periodically to find publishers it may have missed.
    /// </summary>
    [HttpGet("room/{roomId}/participants")]
    public async Task<IActionResult> ListParticipants(long roomId)
    {
        try
        {
            var result = await _janus.ListParticipantsAsync(roomId);
            return Content(result.GetRawText(), "application/json");
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning("listparticipants timeout for room {Room}: {Error}", roomId, ex.Message);
            return StatusCode(504, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "listparticipants error for room {Room}", roomId);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Re-parses a request fragment as a mutable node. Round-tripping the raw text
    /// preserves the payload exactly and avoids reflection-based serialization.
    /// </summary>
    private static JsonNode? ToJsonNode(JsonElement element)
        => element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null
            ? null
            : JsonNode.Parse(element.GetRawText());
}

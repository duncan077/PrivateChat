using System.Text.Json;
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

            var result = await _janus.SendPluginMessageAndWaitAsync(
                sessionId,
                handleId,
                bodyEl,
                jsepEl.HasValue ? jsepEl.Value : null);

            return Content(result.GetRawText(), "application/json");
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
                bodyEl,
                jsepEl.HasValue ? jsepEl.Value : null);

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
    /// Forwards a trickle ICE candidate (or end-of-candidates null) to Janus.
    /// </summary>
    [HttpPost("session/{sessionId}/handle/{handleId}/trickle")]
    public async Task<IActionResult> Trickle(
        long sessionId,
        long handleId,
        [FromBody] JsonElement candidate)
    {
        try
        {
            if (candidate.ValueKind == JsonValueKind.Null)
                await _janus.TrickleCompletedAsync(sessionId, handleId);
            else
                await _janus.TrickleAsync(sessionId, handleId, candidate);

            return Ok();
        }
        catch (Exception ex)
        {
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

            _logger.LogInformation("Poll session {Session}: {Event}", sessionId,
                rawJson.Length > 300 ? rawJson[..300] + "..." : rawJson);

            return Content(rawJson, "application/json");
        }
        catch (Exception ex)
        {
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
        await _janus.SendKeepaliveAsync(sessionId);
        return Ok();
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
}

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

    public JanusController(JanusService janus)
    {
        _janus = janus;
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

            return Ok(result);
        }
        catch (TimeoutException ex)
        {
            return StatusCode(504, new { error = ex.Message });
        }
        catch (Exception ex)
        {
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
    /// </summary>
    [HttpGet("session/{sessionId}/poll")]
    public async Task<IActionResult> LongPoll(long sessionId)
    {
        try
        {
            var result = await _janus.LongPollAsync(sessionId);
            if (result == null)
                return Ok(new { janus = "keepalive" });
            return Ok(result);
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
}

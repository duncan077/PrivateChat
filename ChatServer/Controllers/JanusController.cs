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

    [HttpPost("session/{sessionId}/handle/{handleId}/message")]
    public async Task<IActionResult> SendMessage(long sessionId, long handleId, [FromBody] JsonElement message)
    {
        var body = message.Deserialize<Dictionary<string, object>>() ?? [];
        object? jsep = null;

        if (message.TryGetProperty("jsep", out var jsepEl))
        {
            jsep = jsepEl;
            body.Remove("jsep");
        }

        object msgBody = message.TryGetProperty("body", out var bodyEl) ? bodyEl : message;

        var result = await _janus.SendPluginMessageAsync(sessionId, handleId, msgBody, jsep);
        return Ok(result);
    }

    [HttpPost("session/{sessionId}/handle/{handleId}/trickle")]
    public async Task<IActionResult> Trickle(long sessionId, long handleId, [FromBody] JsonElement candidate)
    {
        if (candidate.ValueKind == JsonValueKind.Null)
            await _janus.TrickleCompletedAsync(sessionId, handleId);
        else
            await _janus.TrickleAsync(sessionId, handleId, candidate);
        return Ok();
    }

    [HttpGet("session/{sessionId}/poll")]
    public async Task<IActionResult> LongPoll(long sessionId)
    {
        var result = await _janus.LongPollAsync(sessionId);
        if (result == null)
            return Ok(new { janus = "keepalive" });
        return Ok(result);
    }
}

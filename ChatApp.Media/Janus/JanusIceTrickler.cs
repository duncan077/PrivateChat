using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace ChatApp.Media.Janus;

/// <summary>
/// Forwards our locally gathered ICE candidates to Janus.
/// <para>
/// This is not an optimisation — it is the only way Janus learns our public address.
/// SIPSorcery snapshots <c>localDescription</c> when <c>setLocalDescription</c> is
/// called, before the STUN round trip that discovers the server-reflexive candidate has
/// finished, and never revises it. Waiting for gathering to complete does not help: the
/// SDP still carries only the host candidates that existed at snapshot time.
/// </para>
/// <para>
/// Left unsent, Janus sees nothing but a LAN address and can never open a connectivity
/// check towards us, so a call connects only when our own checks happen to cross the NAT
/// unaided — which is why exactly one peer connection per session used to survive, and
/// which one looked random.
/// </para>
/// </summary>
internal sealed class JanusIceTrickler
{
    private readonly JanusTransport _transport;
    private readonly long _sessionId;
    private readonly long _handleId;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<RTCIceCandidate> _queued = [];

    private bool _released;
    private int _sent;

    public JanusIceTrickler(
        JanusTransport transport, long sessionId, long handleId, ILogger logger)
    {
        _transport = transport;
        _sessionId = sessionId;
        _handleId = handleId;
        _logger = logger;
    }

    /// <summary>
    /// Starts collecting candidates. Safe to call before the SDP has been sent — anything
    /// gathered early is held until <see cref="ReleaseAsync"/>, because Janus rejects
    /// trickle for a handle that has not yet seen our offer or answer.
    /// </summary>
    public void Attach(RTCPeerConnection peer) => peer.onicecandidate += Enqueue;

    private void Enqueue(RTCIceCandidate? candidate)
    {
        if (candidate is null)
            return;

        lock (_gate)
        {
            if (!_released)
            {
                _queued.Add(candidate);
                return;
            }
        }

        _ = SendAsync(candidate, CancellationToken.None);
    }

    /// <summary>
    /// Sends everything gathered so far and signals end-of-candidates. Call once the SDP
    /// has been delivered to Janus.
    /// </summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        List<RTCIceCandidate> pending;

        lock (_gate)
        {
            if (_released)
                return;

            _released = true;
            pending = [.. _queued];
            _queued.Clear();
        }

        foreach (var candidate in pending)
            await SendAsync(candidate, cancellationToken);

        try
        {
            await _transport.TrickleCompletedAsync(_sessionId, _handleId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Handle {HandleId}: end-of-candidates was not accepted.", _handleId);
        }

        _logger.LogInformation(
            "Handle {HandleId}: trickled {Sent}/{Total} ICE candidate(s) to Janus.",
            _handleId, _sent, pending.Count);
    }

    private async Task SendAsync(RTCIceCandidate candidate, CancellationToken cancellationToken)
    {
        // Janus wants the attribute value, "candidate:...", with no "a=" prefix. Logged
        // verbatim because a wrong shape here is silently ignored by Janus.
        var line = candidate.ToString();

        if (line.StartsWith("a=", StringComparison.Ordinal))
            line = line[2..];

        try
        {
            await _transport.TrickleAsync(
                _sessionId,
                _handleId,
                line,
                candidate.sdpMid,
                candidate.sdpMLineIndex,
                cancellationToken);

            Interlocked.Increment(ref _sent);

            _logger.LogDebug("Handle {HandleId}: trickled '{Candidate}'.", _handleId, line);
        }
        catch (Exception ex)
        {
            // One rejected candidate does not doom the connection; the others may still
            // produce a working pair.
            _logger.LogWarning(
                ex, "Handle {HandleId}: could not trickle a {Type} candidate.", _handleId, candidate.type);
        }
    }
}

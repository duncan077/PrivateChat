using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Janus;

/// <summary>
/// Drives the Janus session: one long-poll loop for events, one timer for keepalives.
/// <para>
/// Port of the polling half of <c>webrtc-manager.js</c>. Two behaviours are load-bearing
/// and preserved: only ever <b>one</b> poll is in flight per session (Janus delivers each
/// event once, so concurrent polls lose events), and a 404 from either poll or keepalive
/// means the session is gone and the app must re-join to get fresh credentials.
/// </para>
/// </summary>
public sealed class JanusEventLoop : IAsyncDisposable
{
    // Janus reaps a session after session_timeout without a keepalive — 60s by default.
    // At 30s a single lost keepalive plus one slow round trip crossed that line, and the
    // server log showed the result: "Poll session <id> not found in Janus" for sessions
    // belonging to live calls, each ending as a full client reconnect. 15s means four
    // consecutive failures are needed before the session can be reaped.
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>Consecutive keepalive failures tolerated before declaring the session lost.</summary>
    private const int MaxKeepaliveFailures = 3;

    private readonly JanusTransport _transport;
    private readonly long _sessionId;
    private readonly ILogger _logger;

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private Task? _keepaliveTask;
    private int _keepaliveFailures;
    private int _sessionLostSignalled;

    /// <summary>Raised for every decoded event, on the poll loop's thread.</summary>
    public event Action<JanusEvent>? EventReceived;

    /// <summary>Raised once when the session is confirmed gone. Never raised twice.</summary>
    public event Action? SessionLost;

    public bool IsRunning => _pollTask is { IsCompleted: false };

    public JanusEventLoop(JanusTransport transport, long sessionId, ILogger? logger = null)
    {
        _transport = transport;
        _sessionId = sessionId;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public void Start()
    {
        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
        _keepaliveTask = Task.Run(() => KeepaliveLoopAsync(_cts.Token));
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        // Backoff applies only to transport faults. A successful poll — including an
        // empty keepalive — resets it, so normal operation never sleeps.
        var backoff = TimeSpan.FromMilliseconds(500);
        var maxBackoff = TimeSpan.FromSeconds(10);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var raw = await _transport.PollAsync(_sessionId, cancellationToken);
                backoff = TimeSpan.FromMilliseconds(500);

                var evt = JanusEventParser.Parse(raw);
                if (evt is JanusKeepaliveEvent)
                    continue;

                try
                {
                    EventReceived?.Invoke(evt);
                }
                catch (Exception ex)
                {
                    // A faulty handler must not kill the only event channel.
                    _logger.LogError(ex, "Janus event handler threw for {EventType}.", evt.GetType().Name);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (JanusSessionLostException)
            {
                SignalSessionLost();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Janus poll failed for session {SessionId}; retrying in {Backoff}.",
                    _sessionId, backoff);

                try
                {
                    await Task.Delay(backoff, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                backoff = backoff < maxBackoff
                    ? TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, maxBackoff.TotalMilliseconds))
                    : maxBackoff;
            }
        }
    }

    private async Task KeepaliveLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(KeepaliveInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await _transport.KeepaliveAsync(_sessionId, cancellationToken);
                    _keepaliveFailures = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (JanusSessionLostException)
                {
                    SignalSessionLost();
                    return;
                }
                catch (Exception ex)
                {
                    // Transient network blips are normal; only a sustained run of
                    // failures means the session is really gone.
                    var failures = Interlocked.Increment(ref _keepaliveFailures);
                    _logger.LogWarning(ex, "Janus keepalive {Failures}/{Max} failed for session {SessionId}.",
                        failures, MaxKeepaliveFailures, _sessionId);

                    if (failures >= MaxKeepaliveFailures)
                    {
                        SignalSessionLost();
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void SignalSessionLost()
    {
        if (Interlocked.Exchange(ref _sessionLostSignalled, 1) == 1)
            return;

        _logger.LogWarning("Janus session {SessionId} lost.", _sessionId);

        try
        {
            SessionLost?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SessionLost handler threw.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
            return;

        await _cts.CancelAsync();

        // The in-flight long poll unblocks via cancellation; wait so callers can rely on
        // no further events firing after DisposeAsync returns.
        var pending = new[] { _pollTask, _keepaliveTask }
            .Where(t => t is not null)
            .Select(t => t!)
            .ToArray();

        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Janus loops did not stop within 5s for session {SessionId}.", _sessionId);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _cts.Dispose();
        _cts = null;
        _pollTask = null;
        _keepaliveTask = null;
    }
}

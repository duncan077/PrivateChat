namespace ChatServer.Services;

/// <summary>
/// Reconciles Janus videoroom codec policy once, shortly after startup.
///
/// This used to run inline between <c>builder.Build()</c> and <c>app.Run()</c>, so
/// Kestrel was not listening for its entire duration - clients got connection-refused
/// while the server talked to a remote gateway one room at a time, each step bounded by
/// a 15s timeout. As a hosted service it runs after the socket is bound, and a failure
/// degrades to "codec policy not synced" instead of "server never started".
///
/// Disable with Janus:RecreateRoomsOnStartup=false.
/// </summary>
public sealed class JanusRoomSyncService : BackgroundService
{
    /// <summary>Ceiling for the whole sweep, so a wedged gateway cannot pin it forever.</summary>
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(2);

    private readonly JanusService _janus;
    private readonly IConfiguration _config;
    private readonly ILogger<JanusRoomSyncService> _logger;

    public JanusRoomSyncService(
        JanusService janus,
        IConfiguration config,
        ILogger<JanusRoomSyncService> logger)
    {
        _janus = janus;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Janus:RecreateRoomsOnStartup", true))
        {
            return;
        }

        // Let the host finish binding before adding load.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(SyncTimeout);

        try
        {
            var recreated = await _janus.RecreateAllVideoRoomsAsync(timeout.Token);
            _logger.LogInformation("Janus startup codec sync completed: recreated {Count} room(s).", recreated);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("Janus startup codec sync timed out after {Timeout}; continuing.", SyncTimeout);
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Janus startup codec sync failed.");
        }
    }
}

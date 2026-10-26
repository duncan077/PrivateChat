namespace ChatApp.Media.Capture;

/// <summary>
/// Decides when a capture that has gone quiet should be torn down and rebuilt.
/// <para>
/// Desktop Duplication stops delivering frames when a fullscreen application takes the
/// display, on a desktop switch, and across a UAC prompt. It reports no error when it
/// does. The share freezes on its last frame and nothing anywhere says so: no frames are
/// produced, so none are encoded, so none are sent, so none are lost, and every statistic
/// on both ends reads healthy. That silence is the whole reason this exists.
/// </para>
/// <para>
/// It watches the clock rather than the device because the device is precisely what has
/// stopped talking — which only works where frames are expected unconditionally. Desktop
/// Duplication repeats the last frame to hold the frame rate, so it qualifies; Windows
/// Graphics Capture sends one only when the screen changes, so a still screen is
/// legitimately silent and no gap proves anything. The backend says which it is by the
/// timeout it hands to <see cref="Started"/>, and a null one turns this off rather than
/// letting it interrupt a share that is working.
/// </para>
/// </summary>
internal sealed class CaptureWatchdog(TimeSpan? restartCooldown = null)
{
    /// <summary>
    /// Held between attempts so a target that genuinely cannot be captured — a monitor
    /// that was unplugged, a window that closed — is retried rather than hammered.
    /// </summary>
    public static readonly TimeSpan DefaultRestartCooldown = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _restartCooldown = restartCooldown ?? DefaultRestartCooldown;
    private readonly Lock _gate = new();

    private bool _watching;
    private bool _restarting;
    private TimeSpan? _stallTimeout;
    private TimeSpan _lastFrameAt;
    private TimeSpan? _lastRestartAt;

    /// <summary>How many times the capture has been rebuilt since sharing began.</summary>
    public long Restarts { get; private set; }

    /// <summary>
    /// Whether an absence of frames means anything for the backend currently running.
    /// False for one that only sends on change, where a quiet window is a quiet screen and
    /// saying otherwise in a log would be wrong.
    /// </summary>
    public bool SilenceIsSuspicious
    {
        get { lock (_gate) return _watching && _stallTimeout is not null; }
    }

    /// <summary>
    /// Begins watching, with the tolerance the backend that just started declares. The
    /// stall clock starts now, so setup is not counted against the capture; a null
    /// <paramref name="stallTimeout"/> watches nothing.
    /// </summary>
    public void Started(TimeSpan now, TimeSpan? stallTimeout)
    {
        lock (_gate)
        {
            _watching = true;
            _stallTimeout = stallTimeout;
            _lastFrameAt = now;
            _lastRestartAt = null;
            Restarts = 0;
        }
    }

    /// <summary>Stops watching, so shutting a share down is never mistaken for a stall.</summary>
    public void Stopped()
    {
        lock (_gate)
            _watching = false;
    }

    public void FrameArrived(TimeSpan now)
    {
        lock (_gate)
        {
            if (now > _lastFrameAt)
                _lastFrameAt = now;
        }
    }

    /// <summary>
    /// Claims the right to restart the capture, if one is due. A caller that gets
    /// <see langword="true"/> owns the restart and must report back through
    /// <see cref="RestartFinished"/>; until it does, no second restart can start.
    /// </summary>
    public bool TryBeginRestart(TimeSpan now, out TimeSpan silence)
    {
        lock (_gate)
        {
            silence = now - _lastFrameAt;

            if (!_watching || _restarting || _stallTimeout is not { } timeout || silence < timeout)
                return false;

            // A restart that has not yet produced a frame looks exactly like the stall it
            // was meant to fix, so the cooldown is what keeps this from looping.
            if (_lastRestartAt is { } last && now - last < _restartCooldown)
                return false;

            _restarting = true;
            _lastRestartAt = now;
            return true;
        }
    }

    /// <summary>
    /// Releases the claim taken by <see cref="TryBeginRestart"/>. A successful restart
    /// also resets the stall clock, giving the new capture the full timeout to produce
    /// its first frame rather than being judged on the old one's silence, and adopts
    /// <paramref name="stallTimeout"/> — a rebuild can land on a different backend to the
    /// one that failed, and then silence means something different than it did.
    /// </summary>
    public void RestartFinished(TimeSpan now, bool succeeded, TimeSpan? stallTimeout = null)
    {
        lock (_gate)
        {
            _restarting = false;

            if (!succeeded)
                return;

            _stallTimeout = stallTimeout;
            Restarts++;
            _lastFrameAt = now;
        }
    }
}

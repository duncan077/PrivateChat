using ChatAppBlazor.Services;
using Shared.Models;

namespace ChatAppBlazor.State;

/// <summary>
/// Screen-share annotation state for the active call.
///
/// A call can carry several shares at once, and each is annotated independently:
/// whoever is sharing owns that share's session, decides whether others may draw on
/// it, and hosts the overlay that renders the ink onto their real desktop. So the
/// store keeps one <see cref="ScreenAnnotationState"/> per sharer rather than one
/// per call. Viewers only capture pointer input and publish normalized coordinates,
/// tagged with the share they were drawn on.
/// </summary>
public partial class CallStore
{
    private ScreenAnnotationCaptureTarget? _localCaptureTarget;

    /// <summary>
    /// Fade preference this client publishes for its own share. Kept separately
    /// from the session so the choice survives toggling annotations off and on.
    /// </summary>
    private int _autoFadeSeconds = ScreenAnnotationState.DefaultAutoFadeSeconds;

    /// <summary>
    /// Every share in this call that currently allows annotations, keyed by sharer.
    /// A sharer with annotations off simply has no entry.
    /// </summary>
    private readonly Dictionary<Guid, ScreenAnnotationState> _annotationSessions = [];

    /// <summary>
    /// This user asked for annotations on their own share and has not turned them
    /// off. Intent, as opposed to the server-confirmed session in
    /// <see cref="_annotationSessions"/>: the two diverge across a hub reconnect,
    /// because losing the last connection makes the server release the session and
    /// broadcast it to a call this client is not listening to at that moment.
    /// </summary>
    private bool _localAnnotationsRequested;

    /// <summary>Active annotation sessions in this call, keyed by sharer user id.</summary>
    public IReadOnlyDictionary<Guid, ScreenAnnotationState> AnnotationSessions => _annotationSessions;

    private Guid LocalUserId => _auth.CurrentUser?.Id ?? Guid.Empty;

    /// <summary>The local user's own share session, or null when they are not hosting one.</summary>
    public ScreenAnnotationState? LocalAnnotationSession
        => _annotationSessions.GetValueOrDefault(LocalUserId);

    /// <summary>True while the local user is hosting an annotation session on their own share.</summary>
    public bool IsAnnotationHost => LocalAnnotationSession is not null;

    /// <summary>True while the local user's own share accepts annotations.</summary>
    public bool LocalAnnotationsEnabled => LocalAnnotationSession?.Enabled == true;

    /// <summary>
    /// Seconds before a finished stroke fades on the local user's own share, or 0
    /// when pinned. Reflects the pending preference when no session is live, so the
    /// presenter's button does not jump back on every toggle.
    /// </summary>
    public int AnnotationAutoFadeSeconds => LocalAnnotationSession?.AutoFadeSeconds ?? _autoFadeSeconds;

    /// <summary>True while strokes on the local user's share stay up until cleared.</summary>
    public bool AnnotationsArePermanent => AnnotationAutoFadeSeconds <= 0;

    /// <summary>
    /// Whether this user's drawing tools are active. Local-only and deliberately
    /// NOT broadcast: each sharer's Enabled flag is the permission for their own
    /// screen, this is the user's own choice to pick up the pen. It also decides
    /// whether the annotation canvases capture pointer input - with tools off,
    /// clicks, zoom and pan reach the video as normal.
    ///
    /// There is exactly one of these per client, not one per stream: however many
    /// annotation-enabled shares are on screen, one toggle and one tool set apply
    /// to all of them.
    /// </summary>
    public bool AnnotationToolsActive { get; private set; }

    /// <summary>Shows or hides this user's drawing tools.</summary>
    public void SetAnnotationToolsActive(bool active)
    {
        if (AnnotationToolsActive == active)
        {
            return;
        }

        AnnotationToolsActive = active;
        // The bridge listens to this event to push canDraw into the JS canvases.
        OnAnnotationStateChanged?.Invoke();
        NotifyChanged();
    }

    /// <summary>True when the local user may draw on at least one shared screen right now.</summary>
    public bool CanAnnotate => IsInCall && _annotationSessions.Count > 0;

    /// <summary>True when this device can render annotations on its own desktop.</summary>
    public bool SupportsDesktopAnnotationOverlay => _annotationOverlay.IsSupported;

    /// <summary>
    /// True when strokes drawn on <paramref name="sharerUserId"/>'s screen will NOT
    /// come back through their video and each client must draw them locally instead
    /// (single-window capture, or a sharer on a platform with no desktop overlay).
    /// </summary>
    public bool AnnotationsNeedLocalRendering(Guid sharerUserId)
    {
        var session = _annotationSessions.GetValueOrDefault(sharerUserId);
        return session is { Enabled: true } && !session.StrokesVisibleInStream;
    }

    /// <summary>Fired when any share's annotation permission or capture details change.</summary>
    public event Action? OnAnnotationStateChanged;

    /// <summary>
    /// Fired for every annotation event received from the hub, including the local
    /// user's own echo. Payload: (fromUserId, annotationEvent).
    /// </summary>
    public event Action<Guid, ScreenAnnotationEvent>? OnAnnotationEvent;

    /// <summary>
    /// Called when the local screen share starts, with the capture target reported
    /// by the WebRTC layer. Resolves the target to a desktop surface and republishes
    /// the local session if annotations are already on.
    /// </summary>
    public async Task SetLocalScreenShareTargetAsync(ScreenAnnotationCaptureTarget target)
    {
        _localCaptureTarget = target;

        var status = _annotationOverlay.SetCaptureTarget(target);

        // With annotations off the resolved surface just sits in the overlay until
        // the presenter turns them on, which publishes it.
        if (LocalAnnotationsEnabled)
        {
            await PublishLocalAnnotationStateAsync(true, status);
        }
    }

    /// <summary>
    /// Called when the local screen share stops. Tears the overlay down and ends the
    /// local user's annotation session; other people's shares are untouched.
    /// </summary>
    public async Task ClearLocalScreenShareTargetAsync()
    {
        _localCaptureTarget = null;
        _annotationOverlay.ClearCaptureTarget();
        _localAnnotationsRequested = false;

        if (LocalAnnotationsEnabled)
        {
            await PublishLocalAnnotationStateAsync(false, ScreenAnnotationOverlayStatus.None);
        }
    }

    /// <summary>
    /// Allow or block annotations on the local user's own shared screen. Never
    /// affects anyone else's share, so two people can present with annotations on
    /// at the same time.
    /// </summary>
    public async Task SetAnnotationsEnabledAsync(bool enabled)
    {
        if (!IsInCall || ActiveRoomId == null)
        {
            return;
        }

        if (enabled && !IsScreenSharing)
        {
            SetError("Start sharing your screen before enabling annotations.");
            return;
        }

        // Re-resolve the surface first: the sharer may have switched monitors, or
        // the frame size may have settled since the share started.
        if (enabled && _localCaptureTarget is { } target)
        {
            _annotationOverlay.SetCaptureTarget(target);
        }

        var status = _annotationOverlay.SetActive(enabled);

        if (!enabled)
        {
            _annotationOverlay.ClearAll();
        }

        _localAnnotationsRequested = enabled;
        await PublishLocalAnnotationStateAsync(enabled, status);
    }

    /// <summary>
    /// Pin strokes on the local user's own share, or let them fade again.
    /// </summary>
    public async Task SetAnnotationsPermanentAsync(bool permanent)
    {
        var seconds = permanent ? 0 : ScreenAnnotationState.DefaultAutoFadeSeconds;
        if (_autoFadeSeconds == seconds && AnnotationAutoFadeSeconds == seconds)
        {
            return;
        }

        _autoFadeSeconds = seconds;
        _annotationOverlay.SetAutoFadeSeconds(seconds);

        // Only meaningful once there is a session to publish it on; otherwise it is
        // remembered and goes out when annotations are enabled.
        if (!IsInCall || !LocalAnnotationsEnabled)
        {
            NotifyChanged();
            return;
        }

        await PublishLocalAnnotationStateAsync(true, _annotationOverlay.Status);
    }

    /// <summary>
    /// Wipes every participant's strokes from the local user's own share. Restricted
    /// to that share's owner by the server, which is what makes it a safe "clean up
    /// my screen" button.
    /// </summary>
    public async Task ClearAllAnnotationsAsync()
    {
        if (!IsInCall || !IsAnnotationHost)
        {
            return;
        }

        _annotationOverlay.ClearAll();
        await SendAnnotationAsync(new ScreenAnnotationEvent
        {
            Kind = ScreenAnnotationEventKind.ClearAll,
            StrokeId = string.Empty,
            TargetSharerUserId = LocalUserId
        });
    }

    /// <summary>
    /// Publishes one annotation event to the call. Failures are swallowed: ink is
    /// high-frequency and a dropped point must never break the call.
    /// </summary>
    public async Task SendAnnotationAsync(ScreenAnnotationEvent annotationEvent)
    {
        if (annotationEvent == null || !IsInCall || ActiveRoomId == null)
        {
            return;
        }

        // Every stroke belongs to exactly one share. Without a live session for it
        // the server would reject the event anyway, so drop it here.
        var target = annotationEvent.TargetSharerUserId;
        if (target == Guid.Empty || _annotationSessions.GetValueOrDefault(target)?.Enabled != true)
        {
            return;
        }

        try
        {
            if (ActiveCallKind == CallContextKind.DirectMessage && ActiveDirectPeerUserId != null)
            {
                await _chat.SendPrivateScreenAnnotationAsync(ActiveDirectPeerUserId.Value, annotationEvent);
            }
            else
            {
                await _chat.SendScreenAnnotationAsync(ActiveRoomId.Value, annotationEvent);
            }
        }
        catch
        {
            // Transient hub errors (reconnect, permission revoked mid-stroke).
        }
    }

    /// <summary>
    /// Pulls every annotation session after joining a call that is already in progress.
    /// </summary>
    public async Task RefreshAnnotationStateAsync()
    {
        if (!IsInCall || ActiveRoomId == null)
        {
            return;
        }

        try
        {
            var sessions = ActiveCallKind == CallContextKind.DirectMessage && ActiveDirectPeerUserId != null
                ? await _chat.GetPrivateScreenAnnotationSessionsAsync(ActiveDirectPeerUserId.Value)
                : await _chat.GetScreenAnnotationSessionsAsync(ActiveRoomId.Value);

            ReplaceAnnotationSessions(sessions ?? []);
        }
        catch
        {
            // Server may not have any sessions yet; state arrives via the hub event.
        }
    }

    /// <summary>
    /// Re-syncs annotations after the hub reconnects.
    ///
    /// Losing the last connection makes the server release this user's session and
    /// broadcast it to the call - an event this client, being the one that dropped,
    /// never sees. Without this the sharer comes back believing annotations are
    /// still on: the toolbar says "Stop annotations", the desktop overlay is still
    /// up, and every stroke is rejected server-side. Sessions belonging to other
    /// sharers can equally have come and gone during the outage, so the server's
    /// list is taken as authoritative and this user's own session re-published if
    /// they still want it.
    /// </summary>
    private async Task ResyncAnnotationsAfterReconnectAsync()
    {
        await RefreshAnnotationStateAsync();

        if (!_localAnnotationsRequested || !IsScreenSharing || LocalAnnotationsEnabled)
        {
            return;
        }

        if (_localCaptureTarget is { } target)
        {
            _annotationOverlay.SetCaptureTarget(target);
        }

        await PublishLocalAnnotationStateAsync(true, _annotationOverlay.SetActive(true));
    }

    /// <summary>Clears annotation state when leaving a call.</summary>
    internal void ResetAnnotationState()
    {
        _localCaptureTarget = null;
        _localAnnotationsRequested = false;
        _annotationOverlay.ClearCaptureTarget();
        AnnotationToolsActive = false;

        if (_annotationSessions.Count > 0)
        {
            _annotationSessions.Clear();
            OnAnnotationStateChanged?.Invoke();
        }
    }

    private async Task PublishLocalAnnotationStateAsync(bool enabled, ScreenAnnotationOverlayStatus status)
    {
        if (!IsInCall || ActiveRoomId == null)
        {
            return;
        }

        var state = new ScreenAnnotationState
        {
            Enabled = enabled,
            SharerUserId = LocalUserId,
            CaptureKind = status.CaptureKind,
            SurfaceWidth = status.SurfaceWidth,
            SurfaceHeight = status.SurfaceHeight,
            OverlayActive = status.OverlayActive,
            AutoFadeSeconds = _autoFadeSeconds
        };

        // Reflect locally right away so the toolbar does not wait for the round trip.
        ApplySessionState(state);

        try
        {
            if (ActiveCallKind == CallContextKind.DirectMessage && ActiveDirectPeerUserId != null)
            {
                await _chat.SetPrivateScreenAnnotationStateAsync(ActiveDirectPeerUserId.Value, state);
            }
            else
            {
                await _chat.SetScreenAnnotationStateAsync(ActiveRoomId.Value, state);
            }
        }
        catch (Exception ex)
        {
            SetError($"Could not update annotation permissions: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies one per-sharer delta. An enabled state adds or replaces that sharer's
    /// session; a disabled one removes it, leaving every other share alone.
    /// </summary>
    private void ApplySessionState(ScreenAnnotationState state)
    {
        var sharerUserId = state.SharerUserId;
        if (sharerUserId == Guid.Empty)
        {
            return;
        }

        var previous = _annotationSessions.GetValueOrDefault(sharerUserId);

        if (state.Enabled)
        {
            _annotationSessions[sharerUserId] = state;
        }
        else if (!_annotationSessions.Remove(sharerUserId))
        {
            // Already gone - a duplicate release, nothing to tell anyone about.
            return;
        }

        if (previous != null && state.Enabled && !HasChanged(previous, state))
        {
            return;
        }

        OnAnnotationStateChanged?.Invoke();
        NotifyChanged();
    }

    private void ReplaceAnnotationSessions(IReadOnlyList<ScreenAnnotationState> sessions)
    {
        _annotationSessions.Clear();
        foreach (var session in sessions)
        {
            if (session is { Enabled: true } && session.SharerUserId != Guid.Empty)
            {
                _annotationSessions[session.SharerUserId] = session;
            }
        }

        // The local session, if the server handed one back, is the authority on the
        // fade the overlay should be running.
        if (LocalAnnotationSession is { } mine)
        {
            _autoFadeSeconds = mine.AutoFadeSeconds;
            _annotationOverlay.SetAutoFadeSeconds(mine.AutoFadeSeconds);
        }

        OnAnnotationStateChanged?.Invoke();
        NotifyChanged();
    }

    private static bool HasChanged(ScreenAnnotationState previous, ScreenAnnotationState next)
        => previous.Enabled != next.Enabled
            || previous.CaptureKind != next.CaptureKind
            || previous.SurfaceWidth != next.SurfaceWidth
            || previous.SurfaceHeight != next.SurfaceHeight
            || previous.OverlayActive != next.OverlayActive
            || previous.AutoFadeSeconds != next.AutoFadeSeconds;

    private void HandleScreenAnnotationStateChanged(Guid contextId, ScreenAnnotationState state)
    {
        if (!IsCurrentCallContext(contextId))
        {
            return;
        }

        var resolved = state ?? new ScreenAnnotationState();

        if (resolved.SharerUserId == LocalUserId && resolved.SharerUserId != Guid.Empty)
        {
            if (resolved.Enabled)
            {
                // Our own session is the only one whose fade our overlay renders.
                _autoFadeSeconds = resolved.AutoFadeSeconds;
                _annotationOverlay.SetAutoFadeSeconds(resolved.AutoFadeSeconds);
            }
            else
            {
                // A disable naming us as sharer tears our overlay down too - this is
                // how a kick or a moderator ending our share reaches the desktop.
                _annotationOverlay.SetActive(false);
                _annotationOverlay.ClearAll();
            }
        }

        ApplySessionState(resolved);
    }

    private void HandleScreenAnnotationReceived(Guid contextId, Guid fromUserId, ScreenAnnotationEvent annotationEvent)
    {
        if (annotationEvent == null || !IsCurrentCallContext(contextId))
        {
            return;
        }

        // Each sharer paints onto their own desktop only the ink aimed at their share.
        var localUserId = LocalUserId;
        if (localUserId != Guid.Empty
            && annotationEvent.TargetSharerUserId == localUserId
            && _annotationOverlay.IsSupported)
        {
            _annotationOverlay.Apply(fromUserId, annotationEvent);
        }

        OnAnnotationEvent?.Invoke(fromUserId, annotationEvent);
    }

    private bool IsCurrentCallContext(Guid contextId)
    {
        if (!IsInCall || contextId == Guid.Empty)
        {
            return false;
        }

        // Room calls key on the room id; private calls key on the derived context id,
        // which the server also uses as the Janus room id handed back at join time.
        return ActiveRoomId == contextId || Session?.RoomId == contextId;
    }
}

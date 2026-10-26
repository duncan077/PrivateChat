using ChatApp.Core.Services;
using ChatApp.Core.State;
using Microsoft.Extensions.Logging;
using Shared.Models;

namespace ChatApp.Core.Media;

/// <summary>
/// Drives <see cref="IMediaEngine"/> from <see cref="CallStore"/> state.
/// <para>
/// This is the native replacement for <c>WebRtcBridge</c>: it translates store settings
/// into <see cref="MediaEngineSettings"/>, starts and stops the engine as calls come and
/// go, restarts it when Janus hands out a new session, and feeds engine events back into
/// the store so the UI reacts.
/// </para>
/// <para>
/// Registered as a singleton. Unlike the Blazor bridge, which was scoped to the WebView,
/// nothing here is tied to a view — a call survives navigation because the host outlives
/// every page.
/// </para>
/// </summary>
public sealed class MediaEngineHost : IAsyncDisposable
{
    private readonly IMediaEngine _engine;
    private readonly CallStore _callStore;
    private readonly AuthStateProvider _auth;
    private readonly GrpcChannelProvider _channelProvider;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);

    private bool _started;
    private int _disposed;

    public IMediaEngine Engine => _engine;

    /// <summary>Participants currently detected as speaking.</summary>
    public event Action<Guid, bool>? SpeakingChanged;

    public MediaEngineHost(
        IMediaEngine engine,
        CallStore callStore,
        AuthStateProvider auth,
        GrpcChannelProvider channelProvider,
        ILogger<MediaEngineHost>? logger = null)
    {
        _engine = engine;
        _callStore = callStore;
        _auth = auth;
        _channelProvider = channelProvider;
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        _callStore.OnJanusReconnect += HandleJanusReconnect;
        _callStore.OnWebRtcSignal += HandleWebRtcSignal;

        _engine.MuteChanged += muted => _callStore.UpdateMuteState(muted);
        _engine.CameraChanged += off => _callStore.UpdateCameraState(off);
        _engine.ScreenShareChanged += sharing => _callStore.UpdateScreenShareState(sharing);
        _engine.PushToTalkChanged += enabled => _callStore.UpdatePushToTalkState(enabled);
        _engine.DevicesChanged += devices => _callStore.UpdateDevices(devices.ToList());
        _engine.ErrorOccurred += message => _callStore.SetError(message);
        _engine.SpeakingChanged += (userId, speaking) => SpeakingChanged?.Invoke(userId, speaking);
        _engine.RemoteStreamAdded += stream => _callStore.EnsureRemoteParticipant(stream.UserId);
        _engine.SessionLost += HandleSessionLost;
    }

    /// <summary>
    /// Joins the room's call and brings the media engine up. Returns false when the
    /// server declined to allocate a Janus session.
    /// </summary>
    public async Task<bool> JoinRoomCallAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        var session = await _callStore.JoinCallAsync(roomId);
        return await StartEngineAsync(session, cancellationToken);
    }

    public async Task<bool> JoinDirectCallAsync(Guid peerUserId, CancellationToken cancellationToken = default)
    {
        var session = await _callStore.JoinPrivateCallAsync(peerUserId);
        return await StartEngineAsync(session, cancellationToken);
    }

    public async Task LeaveCallAsync()
    {
        await _transitionGate.WaitAsync();
        try
        {
            if (_started)
            {
                await _engine.StopAsync("user-left");
                _started = false;
            }
        }
        finally
        {
            _transitionGate.Release();
        }

        await _callStore.LeaveCallAsync();
    }

    private async Task<bool> StartEngineAsync(JanusSessionInfo? session, CancellationToken cancellationToken)
    {
        if (session is null)
        {
            _logger.LogWarning("The server did not return a Janus session; not starting media.");
            return false;
        }

        var user = _auth.CurrentUser;
        if (user is null)
        {
            _logger.LogWarning("No signed-in user; not starting media.");
            return false;
        }

        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            if (_started)
                await _engine.StopAsync("restarting");

            var descriptor = new MediaSessionDescriptor(
                session.JanusSessionId,
                session.JanusHandleId,
                session.JanusRoomId,
                ResolveServerBaseUrl(session.ServerBaseUrl),
                _auth.Token ?? string.Empty,
                user.Id,
                string.IsNullOrWhiteSpace(user.DisplayName) ? user.Id.ToString() : user.DisplayName,
                session.IceServers);

            await _engine.StartAsync(
                descriptor,
                BuildSettings(),
                participantDisplayNames: new Dictionary<string, string>(),
                participantAvatarUrls: new Dictionary<string, string>(),
                cancellationToken);

            _started = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Media engine failed to start.");
            _callStore.SetError($"Could not start the call: {ex.Message}");
            _started = false;
            return false;
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>
    /// Maps the user's persisted call settings onto the engine's settings record.
    /// </summary>
    private MediaEngineSettings BuildSettings()
    {
        var (width, height) = ParseResolution(_callStore.SelectedResolution);

        return new MediaEngineSettings
        {
            MicrophoneDeviceId = _callStore.SelectedMicId,
            CameraDeviceId = _callStore.SelectedCameraId,
            SpeakerDeviceId = _callStore.SelectedSpeakerId,
            Width = width,
            Height = height,
            FrameRate = _callStore.SelectedFps,
            BitrateKbps = _callStore.SelectedBitrate,
            PreferredVideoCodec = ParseCodec(_callStore.SelectedVideoCodec),
            ContentHint = _callStore.SelectedVideoContentHint == "detail"
                ? VideoContentHint.Detail
                : VideoContentHint.Motion,
            H264ProfileOverride = _callStore.H264ProfileOverride,
            NoiseSuppressionEnabled = _callStore.NoiseSuppressionEnabled,
            VoiceGateEnabled = _callStore.VoiceGateEnabled,
            VoiceGateAuto = _callStore.VoiceGateAuto,
            VoiceGateThreshold = _callStore.VoiceGateThreshold,
            ScreenShareAudioEnabled = _callStore.ScreenShareAudioEnabled,
            ScreenShareAutoGainEnabled = _callStore.ScreenShareAutoGainEnabled,
            HideLocalPreview = _callStore.HideLocalPreview,
            MicrophoneVolume = _callStore.Volume,
            PlaybackVolume = _callStore.StreamVolume
        };
    }

    /// <summary>Pushes changed settings into a running engine.</summary>
    public Task ApplyCurrentSettingsAsync() => _engine.ApplySettingsAsync(BuildSettings());

    private static (int Width, int Height) ParseResolution(string? resolution)
    {
        var parts = (resolution ?? string.Empty).Split('x');

        if (parts.Length == 2
            && int.TryParse(parts[0], out var width)
            && int.TryParse(parts[1], out var height))
        {
            return (width, height);
        }

        return (1280, 720);
    }

    private static VideoCodec ParseCodec(string? codec) => (codec ?? string.Empty).ToLowerInvariant() switch
    {
        "h265" or "hvc1" => VideoCodec.H265,
        "av1" => VideoCodec.Av1,
        "vp9" => VideoCodec.Vp9,
        "vp8" => VideoCodec.Vp8,
        _ => VideoCodec.H264
    };

    /// <summary>
    /// The server can report its own base URL as localhost, which is meaningless to a
    /// remote client. Fall back to the address we are already talking to.
    /// </summary>
    private string ResolveServerBaseUrl(string? reported)
    {
        if (Uri.TryCreate(reported, UriKind.Absolute, out var uri)
            && !uri.IsLoopback)
        {
            return uri.GetLeftPart(UriPartial.Authority);
        }

        return _channelProvider.ServerUrl;
    }

    private async void HandleJanusReconnect(JanusSessionInfo session, Guid roomId)
    {
        _logger.LogInformation("Janus handed out a new session; restarting media.");

        try
        {
            await StartEngineAsync(session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restarting media after reconnect failed.");
        }
    }

    private async void HandleWebRtcSignal(Guid fromUserId, string signalType, string payload)
    {
        try
        {
            await _engine.DeliverSignalAsync(fromUserId, signalType, payload);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Delivering a relayed signal failed.");
        }
    }

    private void HandleSessionLost()
    {
        _logger.LogWarning("Janus session lost; asking the store to re-join.");
        _started = false;
        _ = _callStore.HandleJanusSessionLostAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _callStore.OnJanusReconnect -= HandleJanusReconnect;
        _callStore.OnWebRtcSignal -= HandleWebRtcSignal;
        _engine.SessionLost -= HandleSessionLost;

        await _engine.DisposeAsync();
        _transitionGate.Dispose();
    }
}

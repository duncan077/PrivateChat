namespace ChatApp.Core.Media;

/// <summary>
/// The seam that replaces <c>WebRtcBridge</c> + <c>webrtc-manager.js</c>.
/// <para>
/// <see cref="CallStore"/> and the UI talk only to this interface, so the native
/// libdatachannel/FFmpeg engine, a mock used in tests, and any future backend are
/// interchangeable. Implementations own the Janus VideoRoom session, capture,
/// encode, RTP transport, decode and playback.
/// </para>
/// <para>
/// All methods are safe to call from the UI thread; implementations marshal onto
/// their own media threads. Events may be raised from arbitrary threads — the UI
/// layer is responsible for dispatching.
/// </para>
/// </summary>
public interface IMediaEngine : IAsyncDisposable
{
    bool IsRunning { get; }
    bool IsMuted { get; }
    bool IsCameraOff { get; }
    bool IsScreenSharing { get; }

    /// <summary>Join the Janus room described by <paramref name="descriptor"/>.</summary>
    Task StartAsync(
        MediaSessionDescriptor descriptor,
        MediaEngineSettings settings,
        IReadOnlyDictionary<string, string> participantDisplayNames,
        IReadOnlyDictionary<string, string> participantAvatarUrls,
        CancellationToken cancellationToken = default);

    /// <summary>Tear down the session. <paramref name="reason"/> is for diagnostics only.</summary>
    Task StopAsync(string reason = "unspecified", CancellationToken cancellationToken = default);

    Task SetMutedAsync(bool muted);
    Task SetCameraEnabledAsync(bool enabled);

    Task StartScreenShareAsync(ScreenShareTarget target, CancellationToken cancellationToken = default);
    Task StopScreenShareAsync();

    /// <summary>Enumerate the targets a user can pick from in the share picker.</summary>
    Task<IReadOnlyList<ScreenShareTarget>> GetScreenShareTargetsAsync(CancellationToken cancellationToken = default);

    Task ApplySettingsAsync(MediaEngineSettings settings);

    Task<IReadOnlyList<MediaDeviceInfo>> EnumerateDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Codecs this build can actually encode, after probing hardware.</summary>
    Task<IReadOnlyList<VideoCodec>> GetSupportedVideoCodecsAsync();

    Task<VoiceGateLevel> GetVoiceGateLevelAsync();

    /// <summary>Per-participant playback gain, persisted by the UI. 1.0 == unity.</summary>
    Task SetParticipantVolumeAsync(Guid userId, double gain);

    Task SetPushToTalkAsync(bool enabled, string key);

    Task StartMicrophoneTestAsync(string? deviceId);
    Task StopMicrophoneTestAsync();

    Task UpdateParticipantProfilesAsync(
        IReadOnlyDictionary<string, string> displayNames,
        IReadOnlyDictionary<string, string> avatarUrls);

    /// <summary>Deliver a signalling payload relayed through the chat hub.</summary>
    Task DeliverSignalAsync(Guid fromUserId, string signalType, string payload);

    Task<IReadOnlyList<MediaStreamStats>> GetStatsAsync();

    // ─── Engine → app notifications ────────────────────────────────────────────

    event Action<bool>? MuteChanged;
    event Action<bool>? CameraChanged;
    event Action<bool>? ScreenShareChanged;
    event Action<bool>? PushToTalkChanged;

    event Action<IReadOnlyList<MediaDeviceInfo>>? DevicesChanged;

    /// <summary>A remote publisher appeared and its media is now flowing.</summary>
    event Action<RemoteStream>? RemoteStreamAdded;
    event Action<RemoteStream>? RemoteStreamRemoved;

    /// <summary>Speaking-state transitions, replacing the AudioContext analysers.</summary>
    event Action<Guid, bool>? SpeakingChanged;

    /// <summary>Raised when the Janus session is gone and the app must re-join.</summary>
    event Action? SessionLost;

    event Action<string>? ErrorOccurred;
}

/// <summary>A monitor, window or application the user can share.</summary>
public sealed record ScreenShareTarget(
    string Id,
    string Title,
    ScreenShareTargetKind Kind,
    int Width,
    int Height,
    /// <summary>Process id, when the target is a window/app and the platform can capture its audio.</summary>
    uint? ProcessId = null);

public enum ScreenShareTargetKind
{
    Monitor,
    Window,
    Application
}

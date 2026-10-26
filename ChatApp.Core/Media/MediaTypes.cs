namespace ChatApp.Core.Media;

/// <summary>Kind of a capture/playback device.</summary>
public enum MediaDeviceKind
{
    AudioInput,
    AudioOutput,
    VideoInput
}

/// <summary>
/// A capture or playback device. Mirrors the browser MediaDeviceInfo shape the
/// Blazor client used, so persisted device-id preferences keep working.
/// </summary>
public sealed record MediaDeviceInfo(string DeviceId, string Label, string Kind)
{
    public MediaDeviceKind ParsedKind => Kind switch
    {
        "audioinput" => MediaDeviceKind.AudioInput,
        "audiooutput" => MediaDeviceKind.AudioOutput,
        _ => MediaDeviceKind.VideoInput
    };
}

/// <summary>Video codecs the publisher may negotiate, best-first.</summary>
public enum VideoCodec
{
    H264,
    H265,
    Av1,
    Vp9,
    Vp8
}

/// <summary>
/// Hint that drives encoder rate-control tuning. "motion" favours framerate,
/// "detail"/"text" favour spatial quality — the same split the CSS-era
/// contentHint had, but now applied directly to FFmpeg encoder options.
/// </summary>
public enum VideoContentHint
{
    Motion,
    Detail,
    Text
}

/// <summary>Why a remote video tile exists — drives layout and subscribe priority.</summary>
public enum RemoteStreamKind
{
    Camera,
    ScreenShare
}

/// <summary>Settings pushed into the engine whenever the user changes them.</summary>
public sealed record MediaEngineSettings
{
    public string? MicrophoneDeviceId { get; init; }
    public string? CameraDeviceId { get; init; }
    public string? SpeakerDeviceId { get; init; }

    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public int FrameRate { get; init; } = 30;
    public int BitrateKbps { get; init; } = 2000;

    public VideoCodec PreferredVideoCodec { get; init; } = VideoCodec.H264;
    public VideoContentHint ContentHint { get; init; } = VideoContentHint.Motion;
    public string? H264ProfileOverride { get; init; }

    public bool NoiseSuppressionEnabled { get; init; } = true;
    public bool EchoCancellationEnabled { get; init; } = true;
    public bool AutoGainControlEnabled { get; init; } = true;

    public bool VoiceGateEnabled { get; init; }
    public bool VoiceGateAuto { get; init; } = true;
    public int VoiceGateThreshold { get; init; } = 30;

    public bool ScreenShareAudioEnabled { get; init; } = true;
    public bool ScreenShareAutoGainEnabled { get; init; } = true;
    public bool HideLocalPreview { get; init; }

    public double MicrophoneVolume { get; init; } = 1.0;
    public double PlaybackVolume { get; init; } = 1.0;
}

/// <summary>Everything needed to attach to a Janus VideoRoom publisher handle.</summary>
public sealed record MediaSessionDescriptor(
    long JanusSessionId,
    long JanusHandleId,
    long JanusRoomId,
    string ServerBaseUrl,
    string AuthToken,
    Guid UserId,
    string UserDisplayName,
    string IceServersJson);

/// <summary>A remote participant's inbound stream.</summary>
public sealed record RemoteStream(
    Guid UserId,
    long FeedId,
    RemoteStreamKind Kind,
    bool HasVideo,
    bool HasAudio);

/// <summary>Live send/receive statistics for the stream-info panel.</summary>
public sealed record MediaStreamStats(
    long FeedId,
    string Codec,
    int Width,
    int Height,
    double FrameRate,
    long BitrateBps,
    double PacketLossPercent,
    double JitterMs,
    double RoundTripMs);

/// <summary>Current mic level, for the settings-dialog voice-gate meter.</summary>
public readonly record struct VoiceGateLevel(int Level, int Threshold);

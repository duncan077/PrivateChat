namespace ChatApp.Core.Media;

/// <summary>
/// Receives captured audio as interleaved 16-bit PCM. The span is owned by the capture
/// backend and is only valid for the duration of the call.
/// </summary>
public delegate void AudioCapturedHandler(ReadOnlySpan<short> interleaved, int channels, int sampleRate);

/// <summary>
/// Receives a captured video frame. The span is owned by the capture backend and is only
/// valid for the duration of the call — encoders must consume it synchronously.
/// </summary>
public delegate void VideoCapturedHandler(ReadOnlySpan<byte> data, int width, int height, VideoPixelFormat format);

/// <summary>
/// Microphone or system-audio capture.
/// <para>
/// Backends deliver whatever period size the device gives them; the audio pipeline is
/// responsible for repacking into the fixed 20 ms frames Opus requires. Making capture
/// do that would force a second buffer copy in every implementation.
/// </para>
/// </summary>
public interface IAudioCapture : IDisposable
{
    bool IsCapturing { get; }

    /// <summary>Native format of the device, valid once capture has started.</summary>
    int SampleRate { get; }
    int Channels { get; }

    event AudioCapturedHandler? SamplesCaptured;

    /// <summary>Raised when the device disappears (unplugged, or made default elsewhere).</summary>
    event Action<string>? CaptureFailed;

    Task StartAsync(string? deviceId, CancellationToken cancellationToken = default);
    Task StopAsync();
}

/// <summary>Audio playback for the mixed remote streams.</summary>
public interface IAudioPlayback : IDisposable
{
    bool IsPlaying { get; }

    int SampleRate { get; }
    int Channels { get; }

    Task StartAsync(string? deviceId, CancellationToken cancellationToken = default);
    Task StopAsync();

    /// <summary>Queues interleaved PCM for playback. Non-blocking.</summary>
    void Write(ReadOnlySpan<short> interleaved);

    /// <summary>Playback gain, 0.0 to 1.0+ where 1.0 is unity.</summary>
    double Volume { get; set; }
}

/// <summary>Camera capture.</summary>
public interface IVideoCapture : IDisposable
{
    bool IsCapturing { get; }

    int Width { get; }
    int Height { get; }
    int FrameRate { get; }

    event VideoCapturedHandler? FrameCaptured;
    event Action<string>? CaptureFailed;

    Task StartAsync(
        string? deviceId,
        int width,
        int height,
        int frameRate,
        CancellationToken cancellationToken = default);

    Task StopAsync();
}

/// <summary>Screen, window or application capture.</summary>
public interface IScreenCapture : IDisposable
{
    bool IsCapturing { get; }

    int Width { get; }
    int Height { get; }

    event VideoCapturedHandler? FrameCaptured;
    event Action<string>? CaptureFailed;

    Task<IReadOnlyList<ScreenShareTarget>> GetTargetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// How long this backend may produce nothing before it should be presumed dead, or
    /// <see langword="null"/> when silence carries no information and it must never be
    /// restarted for being quiet. Only meaningful once <see cref="StartAsync"/> has run,
    /// since a backend may not know which mechanism it got until then.
    /// <para>
    /// Backends that repeat the last frame to hold a frame rate are never legitimately
    /// silent, so a gap means a capture that has stopped without saying so. Backends that
    /// deliver only on change — Windows Graphics Capture, and screen capture on Wayland —
    /// are silent whenever nobody is doing anything, and restarting them for it would
    /// interrupt a share that is working perfectly.
    /// </para>
    /// </summary>
    TimeSpan? StallTimeout => TimeSpan.FromSeconds(2);

    /// <summary>
    /// Starts capturing a target.
    /// <para>
    /// <paramref name="width"/> and <paramref name="height"/> ask the backend to deliver
    /// frames at that size rather than the target's own, so the resize happens as early as
    /// the platform allows — where it can be fused with the pixel-format conversion the
    /// capture already performs, instead of costing a second pass over a full-resolution
    /// picture later. Zero means "whatever the target is", and a backend that cannot
    /// resize is free to ignore it: callers must cope with frames of any size.
    /// </para>
    /// </summary>
    Task StartAsync(
        ScreenShareTarget target,
        int frameRate,
        int width = 0,
        int height = 0,
        CancellationToken cancellationToken = default);

    Task StopAsync();
}

/// <summary>
/// Enumerates capture and playback devices.
/// <para>
/// Replaces the browser's <c>enumerateDevices</c>. Device ids are backend-specific
/// opaque strings and are persisted in settings, so they must be stable across restarts.
/// </para>
/// </summary>
public interface IMediaDeviceEnumerator
{
    IReadOnlyList<MediaDeviceInfo> GetDevices(MediaDeviceKind kind);

    /// <summary>Raised when devices are added or removed.</summary>
    event Action? DevicesChanged;
}

/// <summary>
/// Creates the platform's capture backends. Each head registers its own.
/// </summary>
public interface ICaptureBackendFactory
{
    IAudioCapture CreateMicrophoneCapture();

    /// <summary>
    /// System/application audio for screen sharing, or null when the platform cannot
    /// capture it (Android without a MediaProjection, most Linux setups without
    /// PipeWire).
    /// </summary>
    IAudioCapture? CreateSystemAudioCapture(ScreenShareTarget? target);

    IAudioPlayback CreatePlayback();

    IVideoCapture CreateCameraCapture();

    /// <summary>Null on platforms with no screen-share support.</summary>
    IScreenCapture? CreateScreenCapture();

    IMediaDeviceEnumerator CreateDeviceEnumerator();
}

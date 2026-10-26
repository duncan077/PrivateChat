using System.Runtime.Versioning;
using ChatApp.Core.Media;
using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Capture.Windows;

/// <summary>
/// Windows capture backends.
/// <para>
/// Audio goes through WASAPI and video through FFmpeg, which is a deliberate split.
/// WASAPI is needed for the microphone because it is the only path that offers loopback
/// (for screen-share audio), a render-side reference signal for echo cancellation, and
/// stable endpoint IDs. Video has none of those constraints, so FFmpeg's <c>dshow</c> and
/// <c>ddagrab</c> replace what would otherwise be Media Foundation and Desktop
/// Duplication interop.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCaptureBackendFactory : ICaptureBackendFactory
{
    private readonly ILogger _logger;

    public WindowsCaptureBackendFactory(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public IAudioCapture CreateMicrophoneCapture() => new WasapiAudioCapture(_logger);

    /// <summary>
    /// System or per-application audio for screen sharing.
    /// <para>
    /// Not implemented yet. This needs WASAPI loopback, and per-process capture needs
    /// the ActivateAudioInterfaceAsync process-loopback path — the same approach the
    /// Blazor client's WindowsScreenShareAudioFallback uses. Returning null makes screen
    /// shares silent rather than failing them.
    /// </para>
    /// </summary>
    public IAudioCapture? CreateSystemAudioCapture(ScreenShareTarget? target)
    {
        _logger.LogWarning("System audio capture is not implemented; the screen share will be silent.");
        return null;
    }

    public IAudioPlayback CreatePlayback() => new WasapiAudioPlayback(_logger);

    public IVideoCapture CreateCameraCapture() => new FFmpegVideoCapture(_logger);

    public IScreenCapture? CreateScreenCapture() => new FFmpegScreenCapture(_logger);

    public IMediaDeviceEnumerator CreateDeviceEnumerator() => new CompositeDeviceEnumerator(_logger);

    /// <summary>
    /// Presents WASAPI audio endpoints and FFmpeg cameras as one device list, so callers
    /// do not have to know that they come from different subsystems.
    /// </summary>
    private sealed class CompositeDeviceEnumerator : IMediaDeviceEnumerator, IDisposable
    {
        private readonly WasapiDeviceEnumerator _audio;
        private readonly FFmpegCameraEnumerator _video;

        public event Action? DevicesChanged
        {
            add => _audio.DevicesChanged += value;
            remove => _audio.DevicesChanged -= value;
        }

        public CompositeDeviceEnumerator(ILogger logger)
        {
            _audio = new WasapiDeviceEnumerator(logger);
            _video = new FFmpegCameraEnumerator(logger);
        }

        public IReadOnlyList<MediaDeviceInfo> GetDevices(MediaDeviceKind kind)
            => kind == MediaDeviceKind.VideoInput
                ? _video.GetDevices(kind)
                : _audio.GetDevices(kind);

        public void Dispose() => _audio.Dispose();
    }
}

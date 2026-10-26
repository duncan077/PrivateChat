using System.Globalization;
using ChatApp.Core.Media;
using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Capture;

/// <summary>
/// Camera capture through FFmpeg — <c>dshow</c> on Windows, <c>v4l2</c> on Linux.
/// <para>
/// Chosen over Media Foundation interop: one implementation covers both platforms, and
/// FFmpeg already decodes the MJPEG most USB cameras fall back to above 720p.
/// </para>
/// </summary>
public sealed class FFmpegVideoCapture : IVideoCapture
{
    private readonly ILogger _logger;
    private readonly FFmpegCaptureSource _source;

    public bool IsCapturing => _source.IsCapturing;
    public int Width => _source.Width;
    public int Height => _source.Height;
    public int FrameRate { get; private set; }

    public event VideoCapturedHandler? FrameCaptured
    {
        add => _source.FrameCaptured += value;
        remove => _source.FrameCaptured -= value;
    }

    public event Action<string>? CaptureFailed
    {
        add => _source.CaptureFailed += value;
        remove => _source.CaptureFailed -= value;
    }

    public FFmpegVideoCapture(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _source = new FFmpegCaptureSource(_logger);
    }

    public Task StartAsync(
        string? deviceId,
        int width,
        int height,
        int frameRate,
        CancellationToken cancellationToken = default)
    {
        FrameRate = frameRate;

        var (format, url) = BuildInput(ResolveDeviceId(deviceId));

        var options = new Dictionary<string, string>
        {
            ["video_size"] = $"{width}x{height}",
            ["framerate"] = frameRate.ToString(CultureInfo.InvariantCulture)
        };

        if (OperatingSystem.IsWindows())
        {
            // Small queue: a camera frame that arrives late is worthless in a call, so
            // drop rather than buffer.
            options["rtbufsize"] = "32M";
        }

        try
        {
            _source.Start(format, url, options);
        }
        catch (Exception ex) when (width != 0 || height != 0)
        {
            // Cameras advertise a fixed mode list and reject anything else. Retry once
            // letting the driver pick, rather than failing the call outright.
            _logger.LogWarning(ex,
                "Camera rejected {Width}x{Height}@{Fps}; retrying at the device default.",
                width, height, frameRate);

            _source.Start(format, url, new Dictionary<string, string>());
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Picks a camera when settings name none.
    /// <para>
    /// DirectShow has no "default device" concept — a name must be supplied, so falling
    /// back to a placeholder produces an I/O error rather than a sensible default. V4L2
    /// does have a conventional first device.
    /// </para>
    /// </summary>
    private string? ResolveDeviceId(string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
            return deviceId;

        if (!OperatingSystem.IsWindows())
            return null;

        var cameras = new FFmpegCameraEnumerator(_logger).GetDevices(Core.Media.MediaDeviceKind.VideoInput);

        if (cameras.Count == 0)
        {
            throw new InvalidOperationException(
                "No camera was found. Connect one, or choose a device in settings.");
        }

        _logger.LogInformation(
            "No camera configured; using '{Camera}'.", cameras[0].Label);

        return cameras[0].DeviceId;
    }

    private static (string Format, string Url) BuildInput(string? deviceId)
    {
        if (OperatingSystem.IsWindows())
        {
            // dshow's "Alternative name" (@device_sw_{...}) is a stable path, unlike the
            // friendly name which collides across two identical cameras.
            return ("dshow", $"video={deviceId}");
        }

        return ("v4l2", string.IsNullOrWhiteSpace(deviceId) ? "/dev/video0" : deviceId);
    }

    public Task StopAsync()
    {
        _source.Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => _source.Dispose();
}

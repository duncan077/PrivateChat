using ChatApp.Core.Media;
using ChatApp.Media.Capture;

namespace ChatApp.Media.Tests;

/// <summary>
/// Exercises the real FFmpeg capture devices. These touch actual hardware, so they are
/// tolerant about frame counts — the point is that frames flow at all, in the right
/// format, and that teardown is clean.
/// </summary>
public class CaptureTests
{
    private sealed record CaptureResult(int Frames, int Width, int Height, VideoPixelFormat Format, string? Error);

    private static async Task<CaptureResult> RunAsync(
        Func<VideoCapturedHandler, Action<string>, Task> start,
        Func<Task> stop,
        int milliseconds)
    {
        var frames = 0;
        var width = 0;
        var height = 0;
        var format = VideoPixelFormat.I420;
        string? error = null;

        void OnFrame(ReadOnlySpan<byte> data, int w, int h, VideoPixelFormat f)
        {
            Interlocked.Increment(ref frames);
            width = w;
            height = h;
            format = f;
        }

        await start(OnFrame, message => error ??= message);
        await Task.Delay(milliseconds);
        await stop();

        return new CaptureResult(frames, width, height, format, error);
    }

    [FFmpegFact]
    public void Enumerates_cameras_with_stable_device_ids()
    {
        var cameras = new FFmpegCameraEnumerator().GetDevices(MediaDeviceKind.VideoInput);

        // A machine with no camera is legitimate, so only assert on what is returned.
        foreach (var camera in cameras)
        {
            Assert.False(string.IsNullOrWhiteSpace(camera.DeviceId));
            Assert.False(string.IsNullOrWhiteSpace(camera.Label));
            Assert.Equal("videoinput", camera.Kind);

            if (OperatingSystem.IsWindows())
            {
                // dshow's alternative-name path, not the friendly name — two identical
                // webcams share a friendly name, and the choice is persisted in settings.
                Assert.StartsWith("@device_", camera.DeviceId, StringComparison.Ordinal);
            }
        }
    }

    [FFmpegFact]
    public void Camera_enumerator_ignores_non_video_kinds()
    {
        var enumerator = new FFmpegCameraEnumerator();

        // dshow reports microphones through the same API; they must not leak in.
        Assert.Empty(enumerator.GetDevices(MediaDeviceKind.AudioInput));
        Assert.Empty(enumerator.GetDevices(MediaDeviceKind.AudioOutput));
    }

    [FFmpegFact]
    public async Task Screen_capture_produces_i420_frames()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var screen = new FFmpegScreenCapture();
        var target = new ScreenShareTarget("0", "Primary", ScreenShareTargetKind.Monitor, 0, 0);

        var result = await RunAsync(
            (onFrame, onError) =>
            {
                screen.FrameCaptured += onFrame;
                screen.CaptureFailed += onError;
                return screen.StartAsync(target, frameRate: 10);
            },
            screen.StopAsync,
            milliseconds: 1200);

        Assert.Null(result.Error);
        Assert.True(result.Frames > 0, "the screen capture graph produced no frames");
        Assert.Equal(VideoPixelFormat.I420, result.Format);
        Assert.True(result.Width > 0 && result.Height > 0);
        Assert.False(screen.IsCapturing);
    }

    [FFmpegFact]
    public async Task Screen_capture_delivers_the_size_it_was_asked_for()
    {
        // The resize lives in the capture graph. A wrong filter name or option there means
        // the graph fails to open and screen sharing breaks outright, which is otherwise
        // silent until someone tries to share.
        if (!OperatingSystem.IsWindows())
            return;

        var monitor = ChatApp.Media.Capture.Windows.WindowsMonitorEnumerator.GetMonitors().FirstOrDefault();
        if (monitor is null || monitor.Width < 640 || monitor.Height < 480)
            return;

        // Half size, rounded even: a request the monitor can definitely satisfy.
        var width = monitor.Width / 2 & ~1;
        var height = monitor.Height / 2 & ~1;

        using var screen = new FFmpegScreenCapture();
        var target = new ScreenShareTarget(
            monitor.OutputIndex.ToString(),
            "Primary",
            ScreenShareTargetKind.Monitor,
            monitor.Width,
            monitor.Height);

        var result = await RunAsync(
            (onFrame, onError) =>
            {
                screen.FrameCaptured += onFrame;
                screen.CaptureFailed += onError;
                return screen.StartAsync(target, frameRate: 10, width, height);
            },
            screen.StopAsync,
            milliseconds: 1500);

        Assert.Null(result.Error);
        Assert.True(result.Frames > 0, "the scaled capture graph produced no frames");

        // The frames must arrive already scaled, or the download and the encoder are still
        // paying for the full-size picture.
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
    }

    [FFmpegFact]
    public async Task Screen_capture_can_restart()
    {
        // Stop/start happens every time a user toggles screen sharing, and a leaked
        // Desktop Duplication handle makes the second attempt fail.
        if (!OperatingSystem.IsWindows())
            return;

        using var screen = new FFmpegScreenCapture();
        var target = new ScreenShareTarget("0", "Primary", ScreenShareTargetKind.Monitor, 0, 0);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await RunAsync(
                (onFrame, onError) =>
                {
                    screen.FrameCaptured += onFrame;
                    screen.CaptureFailed += onError;
                    return screen.StartAsync(target, frameRate: 10);
                },
                screen.StopAsync,
                milliseconds: 800);

            Assert.Null(result.Error);
            Assert.True(result.Frames > 0, $"no frames on attempt {attempt + 1}");
        }
    }

    [FFmpegFact]
    public async Task Camera_capture_produces_i420_frames()
    {
        var cameras = new FFmpegCameraEnumerator().GetDevices(MediaDeviceKind.VideoInput);
        if (cameras.Count == 0)
            return; // No camera on this machine.

        using var camera = new FFmpegVideoCapture();

        var result = await RunAsync(
            (onFrame, onError) =>
            {
                camera.FrameCaptured += onFrame;
                camera.CaptureFailed += onError;
                return camera.StartAsync(cameras[0].DeviceId, 1280, 720, 30);
            },
            camera.StopAsync,
            milliseconds: 1500);

        Assert.Null(result.Error);
        Assert.True(result.Frames > 0, "camera produced no frames");
        Assert.Equal(VideoPixelFormat.I420, result.Format);

        // The buffer must be a full I420 plane set for the reported dimensions, since
        // the encoder indexes into it by plane offset.
        Assert.True(result.Width > 0 && result.Height > 0);
    }

    [FFmpegFact]
    public async Task Stopping_a_capture_that_never_started_is_safe()
    {
        using var screen = new FFmpegScreenCapture();

        await screen.StopAsync();
        Assert.False(screen.IsCapturing);
    }
}

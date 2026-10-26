using Shared.Models;

namespace ChatAppBlazor.Services;

/// <summary>
/// What the local user picked in the screen-share picker, as reported by the
/// WebRTC layer. The label format is browser/WebView specific
/// (WebView2 emits values such as <c>window:81201836:0</c> or <c>screen:0:0</c>).
/// </summary>
public readonly record struct ScreenAnnotationCaptureTarget(
    string TargetLabel,
    string DisplaySurface,
    int FrameWidth,
    int FrameHeight);

/// <summary>
/// Result of resolving a capture target to a real desktop surface.
/// </summary>
public readonly record struct ScreenAnnotationOverlayStatus(
    ScreenAnnotationCaptureKind CaptureKind,
    int SurfaceWidth,
    int SurfaceHeight,
    bool OverlayActive)
{
    public static ScreenAnnotationOverlayStatus None { get; } =
        new(ScreenAnnotationCaptureKind.None, 0, 0, false);

    public static ScreenAnnotationOverlayStatus Unsupported { get; } =
        new(ScreenAnnotationCaptureKind.Unsupported, 0, 0, false);
}

/// <summary>
/// Renders remote participants' annotations on top of the surface this machine is
/// screen sharing, so the strokes appear on the real desktop (and therefore inside
/// the published stream when a whole monitor is captured).
///
/// Windows has a native implementation; other platforms use
/// <see cref="NullScreenAnnotationOverlay"/> and fall back to in-app rendering.
/// </summary>
public interface IScreenAnnotationOverlay
{
    /// <summary>True when this platform can draw on the desktop.</summary>
    bool IsSupported { get; }

    ScreenAnnotationOverlayStatus Status { get; }

    /// <summary>
    /// Points the overlay at the surface currently being shared. Returns the
    /// resolved status (which monitor/window was matched, and its pixel size).
    /// </summary>
    ScreenAnnotationOverlayStatus SetCaptureTarget(ScreenAnnotationCaptureTarget target);

    /// <summary>Stops tracking a capture target and hides the overlay.</summary>
    void ClearCaptureTarget();

    /// <summary>Shows or hides the overlay window without losing existing strokes.</summary>
    ScreenAnnotationOverlayStatus SetActive(bool active);

    /// <summary>
    /// Seconds a finished stroke stays visible before fading out; 0 pins strokes
    /// until they are cleared.
    /// </summary>
    void SetAutoFadeSeconds(int seconds);

    /// <summary>Applies one annotation event from <paramref name="fromUserId"/>.</summary>
    void Apply(Guid fromUserId, ScreenAnnotationEvent annotationEvent);

    /// <summary>Removes every stroke from every participant.</summary>
    void ClearAll();
}

/// <summary>
/// No-op overlay used on platforms without a desktop annotation surface.
/// </summary>
public sealed class NullScreenAnnotationOverlay : IScreenAnnotationOverlay
{
    public bool IsSupported => false;

    public ScreenAnnotationOverlayStatus Status => ScreenAnnotationOverlayStatus.Unsupported;

    public ScreenAnnotationOverlayStatus SetCaptureTarget(ScreenAnnotationCaptureTarget target)
        => ScreenAnnotationOverlayStatus.Unsupported;

    public void ClearCaptureTarget()
    {
    }

    public ScreenAnnotationOverlayStatus SetActive(bool active)
        => ScreenAnnotationOverlayStatus.Unsupported;

    public void SetAutoFadeSeconds(int seconds)
    {
    }

    public void Apply(Guid fromUserId, ScreenAnnotationEvent annotationEvent)
    {
    }

    public void ClearAll()
    {
    }
}

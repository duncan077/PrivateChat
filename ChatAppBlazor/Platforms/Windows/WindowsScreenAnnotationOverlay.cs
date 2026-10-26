#if WINDOWS
using ChatAppBlazor.Services;
using Shared.Models;

namespace ChatAppBlazor.Platforms.Windows;

/// <summary>
/// Windows implementation of <see cref="IScreenAnnotationOverlay"/>.
///
/// Resolves the surface the local user picked in the screen-share dialog to a real
/// monitor or window, then drives <see cref="AnnotationOverlayWindow"/> so remote
/// participants' ink lands on the actual desktop. For whole-monitor shares the
/// overlay sits inside the captured region, so every viewer sees the strokes through
/// the published video; for single-window shares the ink is only visible locally,
/// which is reported back through <see cref="ScreenAnnotationCaptureKind.Window"/>.
/// </summary>
internal sealed class WindowsScreenAnnotationOverlay : IScreenAnnotationOverlay, IDisposable
{
    private readonly object _sync = new();
    private AnnotationOverlayWindow? _window;
    private ScreenAnnotationOverlayStatus _status = ScreenAnnotationOverlayStatus.None;
    private bool _disposed;

    public bool IsSupported => true;

    public ScreenAnnotationOverlayStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public ScreenAnnotationOverlayStatus SetCaptureTarget(ScreenAnnotationCaptureTarget target)
    {
        if (_disposed)
        {
            return ScreenAnnotationOverlayStatus.Unsupported;
        }

        var resolution = CaptureTargetResolver.Resolve(target);
        var window = EnsureWindow();

        switch (resolution.Kind)
        {
            case ScreenAnnotationCaptureKind.Monitor:
                window.SetMonitorTarget(resolution.Bounds);
                break;

            case ScreenAnnotationCaptureKind.Window:
                window.SetWindowTarget(resolution.WindowHandle);
                break;

            default:
                // Nothing to anchor to; keep the overlay hidden.
                window.Hide();
                return UpdateStatus(new ScreenAnnotationOverlayStatus(
                    ScreenAnnotationCaptureKind.Unknown,
                    Math.Max(0, target.FrameWidth),
                    Math.Max(0, target.FrameHeight),
                    false));
        }

        var wasActive = Status.OverlayActive;
        if (wasActive)
        {
            window.Show();
        }

        return UpdateStatus(new ScreenAnnotationOverlayStatus(
            resolution.Kind,
            resolution.Bounds.Width,
            resolution.Bounds.Height,
            wasActive));
    }

    public void ClearCaptureTarget()
    {
        AnnotationOverlayWindow? window;
        lock (_sync)
        {
            window = _window;
            _status = ScreenAnnotationOverlayStatus.None;
        }

        window?.ClearAll();
        window?.Hide();
    }

    public ScreenAnnotationOverlayStatus SetActive(bool active)
    {
        if (_disposed)
        {
            return ScreenAnnotationOverlayStatus.Unsupported;
        }

        var current = Status;
        if (active && current.CaptureKind is not (ScreenAnnotationCaptureKind.Monitor or ScreenAnnotationCaptureKind.Window))
        {
            // No resolved surface — annotations can still be relayed, but this
            // machine cannot paint them on the desktop.
            return UpdateStatus(current with { OverlayActive = false });
        }

        var window = EnsureWindow();
        if (active)
        {
            window.Show();
        }
        else
        {
            window.Hide();
        }

        return UpdateStatus(current with { OverlayActive = active });
    }

    public void SetAutoFadeSeconds(int seconds)
    {
        if (_disposed)
        {
            return;
        }

        EnsureWindow().SetAutoFadeSeconds(seconds);
    }

    public void Apply(Guid fromUserId, ScreenAnnotationEvent annotationEvent)
    {
        if (_disposed || annotationEvent == null)
        {
            return;
        }

        AnnotationOverlayWindow? window;
        lock (_sync)
        {
            window = _window;
        }

        window?.Apply(fromUserId, annotationEvent);
    }

    public void ClearAll()
    {
        AnnotationOverlayWindow? window;
        lock (_sync)
        {
            window = _window;
        }

        window?.ClearAll();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        AnnotationOverlayWindow? window;
        lock (_sync)
        {
            window = _window;
            _window = null;
        }

        window?.Dispose();
    }

    private AnnotationOverlayWindow EnsureWindow()
    {
        lock (_sync)
        {
            return _window ??= new AnnotationOverlayWindow();
        }
    }

    private ScreenAnnotationOverlayStatus UpdateStatus(ScreenAnnotationOverlayStatus status)
    {
        lock (_sync)
        {
            _status = status;
            return status;
        }
    }
}
#endif

#if WINDOWS
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Shared.Models;

namespace ChatAppBlazor.Platforms.Windows;

/// <summary>
/// A transparent, click-through, always-on-top window that paints call participants'
/// annotations directly on the desktop.
///
/// It is a plain Win32 layered window (not WinUI) because <c>UpdateLayeredWindow</c>
/// is the only reliable way to get per-pixel alpha on a top-level window. It never
/// takes focus or input (<c>WS_EX_TRANSPARENT | WS_EX_NOACTIVATE</c>), so the sharer
/// keeps working normally underneath the ink.
///
/// Everything runs on a private thread with its own message pump; public members are
/// safe to call from any thread and are marshalled in through a command queue.
/// </summary>
internal sealed class AnnotationOverlayWindow : IDisposable
{
    private const string WindowClassName = "ChatFlowAnnotationOverlay";
    private const int MinRenderIntervalMs = 16;
    private const int TrackingTimerId = 1;
    private const int RenderTimerId = 2;
    private const int TrackingIntervalMs = 120;

    private static readonly object ClassRegistrationSync = new();
    private static readonly ConcurrentDictionary<IntPtr, AnnotationOverlayWindow> Instances = new();

    // The window class is registered once per process, so the procedure it points
    // at must outlive any single overlay instance.
    private static readonly NativeMethods.WndProc SharedWndProc = StaticWndProc;
    private static bool s_classRegistered;

    private static IntPtr StaticWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        => Instances.TryGetValue(hwnd, out var instance)
            ? instance.OverlayWndProc(hwnd, message, wParam, lParam)
            : NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);

    private readonly object _sync = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly List<OverlayStroke> _strokes = [];
    private readonly Dictionary<string, OverlayStroke> _activeStrokes = new(StringComparer.Ordinal);

    private Thread? _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _memoryDc = IntPtr.Zero;
    private IntPtr _bitmap = IntPtr.Zero;
    private IntPtr _previousBitmap = IntPtr.Zero;
    private IntPtr _bitmapBits = IntPtr.Zero;

    private PixelSurface? _base;
    private PixelSurface? _frame;
    private PixelRect _lastActiveRegion = PixelRect.Empty;
    private bool _baseDirty;
    private bool _renderRequested;
    private bool _renderTimerArmed;
    private long _lastRenderTicks;

    /// <summary>Milliseconds a finished stroke stays fully opaque; 0 pins it.</summary>
    private int _autoFadeMs = ScreenAnnotationState.DefaultAutoFadeSeconds * 1000;

    /// <summary>How long the fade-out itself takes once the delay elapses.</summary>
    private const int FadeDurationMs = 700;

    private NativeMethods.RECT _bounds;
    private long _trackingTicks;
    private IntPtr _trackedWindow = IntPtr.Zero;
    private bool _visible;
    private volatile bool _disposed;

    /// <summary>Current overlay surface size in physical pixels.</summary>
    public (int Width, int Height) SurfaceSize
    {
        get
        {
            lock (_sync)
            {
                return (Math.Max(0, _bounds.Right - _bounds.Left), Math.Max(0, _bounds.Bottom - _bounds.Top));
            }
        }
    }

    public bool IsVisible
    {
        get
        {
            lock (_sync)
            {
                return _visible;
            }
        }
    }

    /// <summary>
    /// Points the overlay at a fixed screen rectangle (whole-monitor capture).
    /// </summary>
    public void SetMonitorTarget(NativeMethods.RECT bounds)
    {
        EnsureThreadStarted();
        Enqueue(() =>
        {
            _trackedWindow = IntPtr.Zero;
            ApplyBounds(bounds);
        });
    }

    /// <summary>
    /// Follows a window as it moves and resizes (single-window capture).
    /// </summary>
    public void SetWindowTarget(IntPtr hwnd)
    {
        EnsureThreadStarted();
        Enqueue(() =>
        {
            _trackedWindow = hwnd;
            if (TryGetTrackedWindowBounds(hwnd, out var bounds))
            {
                ApplyBounds(bounds);
            }
        });
    }

    public void Show()
    {
        EnsureThreadStarted();
        Enqueue(() =>
        {
            lock (_sync)
            {
                _visible = true;
            }

            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            // Establish the layered surface before the window becomes visible,
            // otherwise the first frame can flash undefined content.
            _baseDirty = true;
            _renderRequested = true;
            Render();

            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            BringToFront();
        });
    }

    public void Hide()
    {
        if (_thread == null)
        {
            return;
        }

        Enqueue(() =>
        {
            lock (_sync)
            {
                _visible = false;
            }

            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            }
        });
    }

    /// <summary>
    /// Applies one annotation event. Stroke geometry is stored in normalized
    /// coordinates so it stays correct when the tracked window moves or resizes.
    /// </summary>
    public void Apply(Guid fromUserId, ScreenAnnotationEvent annotationEvent)
    {
        if (annotationEvent == null)
        {
            return;
        }

        EnsureThreadStarted();
        Enqueue(() => ApplyCore(fromUserId, annotationEvent));
    }

    public void ClearAll()
    {
        if (_thread == null)
        {
            return;
        }

        Enqueue(() =>
        {
            _strokes.Clear();
            _activeStrokes.Clear();
            _baseDirty = true;
            RequestRender();
        });
    }

    /// <summary>
    /// Sets how long finished strokes remain before fading. Passing 0 pins existing
    /// ink, which also restarts the fade clock of anything already on screen so a
    /// stroke that was mid-fade does not vanish right after being pinned.
    /// </summary>
    public void SetAutoFadeSeconds(int seconds)
    {
        EnsureThreadStarted();
        Enqueue(() =>
        {
            var next = Math.Max(0, seconds) * 1000;
            if (_autoFadeMs == next)
            {
                return;
            }

            var wasFading = _autoFadeMs > 0;
            _autoFadeMs = next;

            if (next == 0 && wasFading)
            {
                // Pinning: anything partially faded returns to full opacity.
                var now = Environment.TickCount64;
                foreach (var stroke in _strokes)
                {
                    stroke.CompletedAtTicks = now;
                }
            }

            _baseDirty = true;
            RequestRender();
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var thread = _thread;
        if (thread == null)
        {
            return;
        }

        Enqueue(() =>
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_hwnd);
            }
        });

        thread.Join(TimeSpan.FromMilliseconds(750));
        _thread = null;
        _ready.Dispose();
    }

    // ─── Command plumbing ──────────────────────────────────────────────────────

    private void EnsureThreadStarted()
    {
        if (_disposed || _thread != null)
        {
            return;
        }

        lock (_sync)
        {
            if (_thread != null)
            {
                return;
            }

            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "AnnotationOverlay"
            };
            _thread.Start();
        }

        // Wait briefly so the first queued command has a window to act on.
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void Enqueue(Action command)
    {
        _commands.Enqueue(command);

        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_APP_COMMAND, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            try
            {
                command();
            }
            catch
            {
                // One bad stroke must never kill the overlay pump.
            }
        }
    }

    private void ThreadMain()
    {
        try
        {
            if (!CreateOverlayWindow())
            {
                _ready.Set();
                return;
            }

            NativeMethods.SetTimer(_hwnd, TrackingTimerId, TrackingIntervalMs, IntPtr.Zero);
            _ready.Set();

            // Commands queued before the window existed never got a wake-up post.
            DrainCommands();

            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        catch
        {
            // Overlay is best-effort; the call must continue without it.
        }
        finally
        {
            _ready.Set();
            ReleaseGraphicsResources();
            _hwnd = IntPtr.Zero;
        }
    }

    private bool CreateOverlayWindow()
    {
        var moduleHandle = NativeMethods.GetModuleHandle(null);

        lock (ClassRegistrationSync)
        {
            if (!s_classRegistered)
            {
                var windowClass = new NativeMethods.WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(SharedWndProc),
                    hInstance = moduleHandle,
                    lpszClassName = WindowClassName
                };

                if (NativeMethods.RegisterClassEx(ref windowClass) == 0 &&
                    Marshal.GetLastWin32Error() != NativeMethods.ERROR_CLASS_ALREADY_EXISTS)
                {
                    return false;
                }

                s_classRegistered = true;
            }
        }

        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_LAYERED |
            NativeMethods.WS_EX_TRANSPARENT |
            NativeMethods.WS_EX_TOPMOST |
            NativeMethods.WS_EX_TOOLWINDOW |
            NativeMethods.WS_EX_NOACTIVATE,
            WindowClassName,
            string.Empty,
            NativeMethods.WS_POPUP,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            moduleHandle,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            return false;
        }

        Instances[_hwnd] = this;
        return true;
    }

    private IntPtr OverlayWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_APP_COMMAND:
                DrainCommands();
                return IntPtr.Zero;

            case NativeMethods.WM_TIMER:
                if (wParam == TrackingTimerId)
                {
                    TrackTargetWindow();
                }
                else if (wParam == RenderTimerId)
                {
                    NativeMethods.KillTimer(hwnd, RenderTimerId);
                    _renderTimerArmed = false;
                    Render();
                }

                return IntPtr.Zero;

            case NativeMethods.WM_DESTROY:
                Instances.TryRemove(hwnd, out _);
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    // ─── Target tracking ───────────────────────────────────────────────────────

    private void TrackTargetWindow()
    {
        _trackingTicks++;

        // Strokes sitting out their delay produce no events, so nothing else would
        // wake the renderer to notice they are due to start fading.
        if (_autoFadeMs > 0 && _strokes.Count > 0)
        {
            RequestRender();
        }

        if (_trackedWindow == IntPtr.Zero)
        {
            // Monitor capture: periodically make sure nothing covered the overlay.
            ReassertTopmost();
            return;
        }

        if (!NativeMethods.IsWindow(_trackedWindow))
        {
            _trackedWindow = IntPtr.Zero;
            Hide();
            return;
        }

        if (TryGetTrackedWindowBounds(_trackedWindow, out var bounds) && !RectEquals(bounds, _bounds))
        {
            ApplyBounds(bounds);
        }

        ReassertTopmost();
    }

    /// <summary>
    /// Other top-most windows can appear above the overlay, so its z-order is
    /// refreshed occasionally — but not on every tracking tick, which would keep
    /// churning the global window order.
    /// </summary>
    private void ReassertTopmost()
    {
        if (_visible && _trackingTicks % 4 == 0)
        {
            BringToFront();
        }
    }

    /// <summary>
    /// Uses the DWM extended frame bounds, which exclude the drop shadow and match
    /// what Windows window-capture actually records.
    /// </summary>
    private static bool TryGetTrackedWindowBounds(IntPtr hwnd, out NativeMethods.RECT bounds)
    {
        bounds = default;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        var size = Marshal.SizeOf<NativeMethods.RECT>();
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var frameBounds,
                size) == 0 &&
            frameBounds.Right > frameBounds.Left &&
            frameBounds.Bottom > frameBounds.Top)
        {
            bounds = frameBounds;
            return true;
        }

        return NativeMethods.GetWindowRect(hwnd, out bounds) &&
               bounds.Right > bounds.Left &&
               bounds.Bottom > bounds.Top;
    }

    private static bool RectEquals(NativeMethods.RECT a, NativeMethods.RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private void ApplyBounds(NativeMethods.RECT bounds)
    {
        var width = Math.Max(1, bounds.Right - bounds.Left);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);

        var sizeChanged = _base == null || _base.Width != width || _base.Height != height;

        lock (_sync)
        {
            _bounds = bounds;
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                _hwnd,
                NativeMethods.HWND_TOPMOST,
                bounds.Left,
                bounds.Top,
                width,
                height,
                NativeMethods.SWP_NOACTIVATE);
        }

        if (sizeChanged)
        {
            RecreateGraphicsResources(width, height);
        }

        _baseDirty = true;
        RequestRender();
    }

    private void BringToFront()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // ─── Stroke model ──────────────────────────────────────────────────────────

    private static string BuildStrokeKey(Guid owner, string? strokeId)
        => $"{owner:N}:{strokeId ?? string.Empty}";

    private void ApplyCore(Guid fromUserId, ScreenAnnotationEvent annotationEvent)
    {
        switch (annotationEvent.Kind)
        {
            case ScreenAnnotationEventKind.Begin:
            {
                var key = BuildStrokeKey(fromUserId, annotationEvent.StrokeId);
                var stroke = new OverlayStroke
                {
                    Owner = fromUserId,
                    StrokeId = annotationEvent.StrokeId ?? string.Empty,
                    Tool = annotationEvent.Tool,
                    Color = annotationEvent.Color,
                    Width = annotationEvent.Width
                };
                stroke.AppendPoints(annotationEvent.Points);

                _activeStrokes[key] = stroke;
                RequestRender();
                return;
            }

            case ScreenAnnotationEventKind.Points:
            {
                var key = BuildStrokeKey(fromUserId, annotationEvent.StrokeId);
                if (!_activeStrokes.TryGetValue(key, out var stroke))
                {
                    // A late join can miss the Begin; treat the first update as one.
                    stroke = new OverlayStroke
                    {
                        Owner = fromUserId,
                        StrokeId = annotationEvent.StrokeId ?? string.Empty,
                        Tool = annotationEvent.Tool,
                        Color = annotationEvent.Color,
                        Width = annotationEvent.Width
                    };
                    _activeStrokes[key] = stroke;
                }

                if (stroke.Tool is ScreenAnnotationTool.Rectangle
                    or ScreenAnnotationTool.Ellipse
                    or ScreenAnnotationTool.Arrow)
                {
                    // Shapes are defined by their first and current point only.
                    RewriteShapePoints(stroke, annotationEvent.Points);
                }
                else
                {
                    stroke.AppendPoints(annotationEvent.Points);
                }

                RequestRender();
                return;
            }

            case ScreenAnnotationEventKind.End:
            {
                var key = BuildStrokeKey(fromUserId, annotationEvent.StrokeId);
                if (!_activeStrokes.Remove(key, out var stroke))
                {
                    return;
                }

                if (stroke.Tool is ScreenAnnotationTool.Rectangle
                    or ScreenAnnotationTool.Ellipse
                    or ScreenAnnotationTool.Arrow)
                {
                    RewriteShapePoints(stroke, annotationEvent.Points);
                }
                else
                {
                    stroke.AppendPoints(annotationEvent.Points);
                }

                if (stroke.Points.Count >= 2)
                {
                    stroke.Completed = true;
                    // The fade clock starts when the stroke is finished, not when
                    // it was begun, so a slow deliberate stroke gets its full time.
                    stroke.CompletedAtTicks = Environment.TickCount64;
                    _strokes.Add(stroke);
                }

                _baseDirty = true;
                RequestRender();
                return;
            }

            case ScreenAnnotationEventKind.Undo:
            {
                for (var i = _strokes.Count - 1; i >= 0; i--)
                {
                    if (_strokes[i].Owner == fromUserId)
                    {
                        _strokes.RemoveAt(i);
                        break;
                    }
                }

                _baseDirty = true;
                RequestRender();
                return;
            }

            case ScreenAnnotationEventKind.Clear:
            {
                _strokes.RemoveAll(stroke => stroke.Owner == fromUserId);
                foreach (var key in _activeStrokes
                             .Where(pair => pair.Value.Owner == fromUserId)
                             .Select(pair => pair.Key)
                             .ToList())
                {
                    _activeStrokes.Remove(key);
                }

                _baseDirty = true;
                RequestRender();
                return;
            }

            case ScreenAnnotationEventKind.ClearAll:
            {
                _strokes.Clear();
                _activeStrokes.Clear();
                _baseDirty = true;
                RequestRender();
                return;
            }
        }
    }

    private static void RewriteShapePoints(OverlayStroke stroke, float[]? points)
    {
        if (points == null || points.Length < 2)
        {
            return;
        }

        // Keep the anchor point, replace the trailing point with the latest one.
        if (stroke.Points.Count >= 2)
        {
            var anchorX = stroke.Points[0];
            var anchorY = stroke.Points[1];
            stroke.Points.Clear();
            stroke.Points.Add(anchorX);
            stroke.Points.Add(anchorY);
        }

        stroke.AppendPoints([points[^2], points[^1]]);
    }

    // ─── Rendering ─────────────────────────────────────────────────────────────

    private void RequestRender()
    {
        _renderRequested = true;

        if (_hwnd == IntPtr.Zero || !_visible)
        {
            return;
        }

        var elapsedMs = (Environment.TickCount64 - _lastRenderTicks);
        if (elapsedMs >= MinRenderIntervalMs)
        {
            Render();
            return;
        }

        if (!_renderTimerArmed)
        {
            _renderTimerArmed = true;
            NativeMethods.SetTimer(_hwnd, RenderTimerId, (uint)Math.Max(1, MinRenderIntervalMs - elapsedMs), IntPtr.Zero);
        }
    }

    private void Render()
    {
        if (!_renderRequested || _hwnd == IntPtr.Zero || !_visible)
        {
            return;
        }

        var baseSurface = _base;
        var frameSurface = _frame;
        if (baseSurface == null || frameSurface == null || _bitmapBits == IntPtr.Zero)
        {
            return;
        }

        _renderRequested = false;
        _lastRenderTicks = Environment.TickCount64;

        // Expire and fade finished strokes. Anything fully transparent is dropped
        // so the stroke list cannot grow without bound over a long session.
        var now = Environment.TickCount64;
        var anyFading = false;

        if (_autoFadeMs > 0 && _strokes.Count > 0)
        {
            var removed = _strokes.RemoveAll(s => s.GetOpacity(now, _autoFadeMs, FadeDurationMs) <= 0f);
            if (removed > 0)
            {
                _baseDirty = true;
            }

            foreach (var stroke in _strokes)
            {
                if (stroke.GetOpacity(now, _autoFadeMs, FadeDurationMs) < 1f)
                {
                    anyFading = true;
                    // A partially transparent stroke changes every frame, so the
                    // base layer has to be rebuilt rather than reused.
                    _baseDirty = true;
                    break;
                }
            }
        }

        if (_baseDirty)
        {
            baseSurface.Clear();
            foreach (var stroke in _strokes)
            {
                AnnotationRasterizer.Draw(baseSurface, stroke, stroke.GetOpacity(now, _autoFadeMs, FadeDurationMs));
            }

            _baseDirty = false;
            _lastActiveRegion = new PixelRect(0, 0, baseSurface.Width, baseSurface.Height);
        }

        // Restore the rows that in-progress strokes touched last frame, then redraw
        // them; completed ink lives in the base surface and never needs repainting.
        var refreshRegion = _lastActiveRegion;
        var activeRegion = PixelRect.Empty;

        foreach (var stroke in _activeStrokes.Values)
        {
            activeRegion = activeRegion.Union(AnnotationRasterizer.GetBounds(stroke, frameSurface.Width, frameSurface.Height));
        }

        refreshRegion = refreshRegion.Union(activeRegion);
        if (refreshRegion.IsEmpty)
        {
            refreshRegion = new PixelRect(0, 0, frameSurface.Width, frameSurface.Height);
        }

        frameSurface.CopyRowsFrom(baseSurface, refreshRegion.Top, refreshRegion.Bottom);

        foreach (var stroke in _activeStrokes.Values)
        {
            AnnotationRasterizer.Draw(frameSurface, stroke);
        }

        _lastActiveRegion = activeRegion;

        PublishFrame(frameSurface, refreshRegion);

        // Fading is an animation: keep asking for frames until it finishes.
        // RequestRender throttles to ~60fps, so this does not spin.
        if (anyFading)
        {
            RequestRender();
        }
    }

    private void PublishFrame(PixelSurface surface, PixelRect changedRows)
    {
        var top = Math.Clamp(changedRows.Top, 0, surface.Height);
        var bottom = Math.Clamp(changedRows.Bottom, 0, surface.Height);
        if (bottom > top)
        {
            var offset = top * surface.Width;
            var count = (bottom - top) * surface.Width;
            Marshal.Copy(surface.Pixels, offset, IntPtr.Add(_bitmapBits, offset * 4), count);
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.RECT bounds;
            lock (_sync)
            {
                bounds = _bounds;
            }

            var destination = new NativeMethods.POINT { X = bounds.Left, Y = bounds.Top };
            var size = new NativeMethods.SIZE { Cx = surface.Width, Cy = surface.Height };
            var source = new NativeMethods.POINT { X = 0, Y = 0 };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };

            NativeMethods.UpdateLayeredWindow(
                _hwnd,
                screenDc,
                ref destination,
                ref size,
                _memoryDc,
                ref source,
                0,
                ref blend,
                NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void RecreateGraphicsResources(int width, int height)
    {
        ReleaseGraphicsResources();

        _base = new PixelSurface(width, height);
        _frame = new PixelSurface(width, height);
        _lastActiveRegion = PixelRect.Empty;

        _memoryDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (_memoryDc == IntPtr.Zero)
        {
            return;
        }

        var header = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                // Negative height selects a top-down bitmap, matching our row order.
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB
            }
        };

        _bitmap = NativeMethods.CreateDIBSection(
            _memoryDc,
            ref header,
            NativeMethods.DIB_RGB_COLORS,
            out _bitmapBits,
            IntPtr.Zero,
            0);

        if (_bitmap == IntPtr.Zero)
        {
            _bitmapBits = IntPtr.Zero;
            return;
        }

        _previousBitmap = NativeMethods.SelectObject(_memoryDc, _bitmap);
    }

    private void ReleaseGraphicsResources()
    {
        if (_memoryDc != IntPtr.Zero)
        {
            if (_previousBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_memoryDc, _previousBitmap);
                _previousBitmap = IntPtr.Zero;
            }

            NativeMethods.DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        if (_bitmap != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
        }

        _bitmapBits = IntPtr.Zero;
    }
}
#endif

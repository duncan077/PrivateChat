using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ChatApp.Core.Media;

namespace ChatApp.Ui.Controls;

/// <summary>
/// Draws an <see cref="IVideoSource"/>. The replacement for the <c>&lt;video&gt;</c>
/// elements the Blazor client handed to the WebView.
/// <para>
/// Frames arrive on a decode thread and may not outlive the callback, so each one is
/// converted straight into a BGRA buffer there and handed to the UI thread through a
/// single-slot swap. When the UI cannot keep up the pending frame is overwritten rather
/// than queued: for live video the newest frame is the only one worth drawing.
/// </para>
/// <para>
/// Conversion targets the size this control will actually draw, so a 1080p feed in a
/// thumbnail scales once inside swscale instead of moving four times the pixels through
/// the bitmap upload and the GPU.
/// </para>
/// </summary>
public sealed class VideoView : Control
{
    /// <summary>
    /// Supplies the scaler. Set once at startup by the head, because a control created
    /// from XAML cannot take constructor dependencies.
    /// </summary>
    public static IVideoFrameConverterFactory? ConverterFactory { get; set; }

    /// <summary>How long without a frame before the tile falls back to its placeholder.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(2);

    public static readonly StyledProperty<IVideoSource?> SourceProperty =
        AvaloniaProperty.Register<VideoView, IVideoSource?>(nameof(Source));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<VideoView, Stretch>(nameof(Stretch), Stretch.Uniform);

    public static readonly DirectProperty<VideoView, bool> IsRenderingProperty =
        AvaloniaProperty.RegisterDirect<VideoView, bool>(
            nameof(IsRendering), o => o.IsRendering);

    /// <summary>The feed to draw. Changing it detaches from the previous one.</summary>
    public IVideoSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>
    /// How the picture fills the control. <see cref="Stretch.Uniform"/> letterboxes,
    /// <see cref="Stretch.UniformToFill"/> crops — the usual choice for grid tiles.
    /// </summary>
    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    private bool _isRendering;

    /// <summary>
    /// True while frames are arriving. Goes false after <see cref="StallTimeout"/>, so a
    /// participant switching their camera off leaves an avatar rather than a frozen
    /// picture of them.
    /// </summary>
    public bool IsRendering
    {
        get => _isRendering;
        private set => SetAndRaise(IsRenderingProperty, ref _isRendering, value);
    }

    // Guards the frame handoff. Held only for a pointer swap, never during conversion.
    private readonly Lock _gate = new();

    // Guards the converter against disposal while a conversion is in flight — detaching
    // unsubscribes but cannot recall a callback that has already started.
    private readonly Lock _converterGate = new();

    private byte[]? _pending;
    private byte[]? _spare;
    private int _pendingWidth;
    private int _pendingHeight;
    private int _pendingStride;
    private int _postPending;

    /// <summary>
    /// Desired conversion size in device pixels, width in the high half. Packed into one
    /// long so the decode thread reads both halves atomically.
    /// </summary>
    private long _target;

    private IVideoFrameConverter? _converter;
    private WriteableBitmap? _bitmap;
    private DispatcherTimer? _stallTimer;
    private IVideoSource? _attachedSource;
    private Stretch _stretch = Stretch.Uniform;
    private bool _isAttached;
    private long _lastFrameTicks;

    public VideoView()
    {
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _isAttached = true;
        UpdateSubscription();

        _stallTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1), DispatcherPriority.Background, HandleStallTick);
        _stallTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _isAttached = false;
        UpdateSubscription();

        _stallTimer?.Stop();
        _stallTimer = null;

        // Release the scaler and the bitmap: a tile that is no longer on screen should
        // not hold a full-resolution surface for the rest of the call.
        lock (_converterGate)
        {
            _converter?.Dispose();
            _converter = null;
        }

        _bitmap?.Dispose();
        _bitmap = null;

        lock (_gate)
        {
            _pending = null;
            _spare = null;
        }

        IsRendering = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            UpdateSubscription();
        }
        else if (change.Property == StretchProperty)
        {
            // Mirrored into a field: the decode thread sizes each frame from it, and
            // Avalonia properties may only be read on the UI thread.
            _stretch = Stretch;
            InvalidateVisual();
        }
        else if (change.Property == IsVisibleProperty && !IsVisible)
        {
            // A hidden control is never arranged, so without this it would keep asking
            // for conversions at whatever size it had when it was last shown.
            Interlocked.Exchange(ref _target, 0);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

        var width = (int)Math.Ceiling(finalSize.Width * scaling);
        var height = (int)Math.Ceiling(finalSize.Height * scaling);

        Interlocked.Exchange(ref _target, ((long)width << 32) | (uint)height);

        return base.ArrangeOverride(finalSize);
    }

    private void UpdateSubscription()
    {
        var wanted = _isAttached ? Source : null;

        if (ReferenceEquals(wanted, _attachedSource))
            return;

        if (_attachedSource is not null)
        {
            _attachedSource.FrameReady -= HandleFrameReady;
            _attachedSource.ResolutionChanged -= HandleResolutionChanged;
        }

        _attachedSource = wanted;
        IsRendering = false;

        if (_attachedSource is null)
        {
            InvalidateVisual();
            return;
        }

        EnsureConverter();

        _attachedSource.FrameReady += HandleFrameReady;
        _attachedSource.ResolutionChanged += HandleResolutionChanged;
    }

    private void EnsureConverter()
    {
        lock (_converterGate)
        {
            if (_converter is not null)
                return;

            try
            {
                _converter = ConverterFactory?.Create();
            }
            catch (Exception)
            {
                // No FFmpeg, so nothing is decoding either. The tile shows its
                // placeholder rather than taking the app down.
                _converter = null;
            }
        }
    }

    /// <summary>
    /// Converts one frame on the decode thread and publishes it for the UI thread.
    /// </summary>
    private void HandleFrameReady(IVideoSource source, in VideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return;

        var (targetWidth, targetHeight) = ComputeTargetSize(frame.Width, frame.Height);
        if (targetWidth <= 0 || targetHeight <= 0)
            return;

        var stride = targetWidth * 4;
        var required = stride * targetHeight;

        byte[]? buffer;
        lock (_gate)
        {
            buffer = _spare;
            _spare = null;
        }

        if (buffer is null || buffer.Length < required)
            buffer = new byte[required];

        bool converted;
        lock (_converterGate)
        {
            converted = _converter is not null
                && _converter.Convert(frame, targetWidth, targetHeight, buffer, stride);
        }

        if (!converted)
        {
            // Give the buffer back so a failed frame does not force a reallocation.
            lock (_gate)
                _spare ??= buffer;

            return;
        }

        lock (_gate)
        {
            // Whatever was waiting has been superseded. Recycling it here is what keeps
            // the steady state at two buffers no matter how far behind the UI falls.
            _spare = _pending;
            _pending = buffer;
            _pendingWidth = targetWidth;
            _pendingHeight = targetHeight;
            _pendingStride = stride;
        }

        if (Interlocked.Exchange(ref _postPending, 1) == 0)
            Dispatcher.UIThread.Post(PresentPending, DispatcherPriority.Render);
    }

    /// <summary>
    /// Picks the conversion size: the drawn size, never larger than the source.
    /// <para>
    /// Widths are quantized so that dragging a window edge does not rebuild the scaler on
    /// every pixel; height follows from the source aspect, which keeps the letterboxing
    /// honest to within a pixel.
    /// </para>
    /// </summary>
    private (int Width, int Height) ComputeTargetSize(int sourceWidth, int sourceHeight)
    {
        var packed = Interlocked.Read(ref _target);
        var boundsWidth = (int)(packed >> 32);
        var boundsHeight = (int)(uint)packed;

        if (boundsWidth <= 0 || boundsHeight <= 0)
            return (0, 0);

        var horizontal = boundsWidth / (double)sourceWidth;
        var vertical = boundsHeight / (double)sourceHeight;

        var scale = _stretch switch
        {
            // Both crop or stretch, so the limiting dimension is the larger ratio.
            Stretch.UniformToFill or Stretch.Fill => Math.Max(horizontal, vertical),
            Stretch.None => 1.0,
            _ => Math.Min(horizontal, vertical)
        };

        // Upscaling here would cost memory bandwidth for detail that does not exist; the
        // GPU can stretch the smaller bitmap for free.
        scale = Math.Min(scale, 1.0);

        const int Quantum = 16;

        var width = (int)Math.Round(sourceWidth * scale);
        width = Math.Clamp(
            (width + Quantum - 1) / Quantum * Quantum, Quantum, Math.Max(sourceWidth, Quantum));

        var height = (int)Math.Round(width * (double)sourceHeight / sourceWidth);
        height = Math.Clamp(height + (height & 1), 2, Math.Max(sourceHeight, 2));

        return (width, height);
    }

    private void PresentPending()
    {
        Interlocked.Exchange(ref _postPending, 0);

        byte[]? buffer;
        int width, height, stride;

        lock (_gate)
        {
            buffer = _pending;
            _pending = null;
            width = _pendingWidth;
            height = _pendingHeight;
            stride = _pendingStride;
        }

        if (buffer is null)
            return;

        if (_isAttached)
        {
            Upload(buffer, width, height, stride);

            _lastFrameTicks = Environment.TickCount64;
            IsRendering = true;
            InvalidateVisual();
        }

        lock (_gate)
            _spare = buffer;
    }

    private void Upload(byte[] buffer, int width, int height, int stride)
    {
        if (_bitmap is null
            || _bitmap.PixelSize.Width != width
            || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,

                // The converter always writes opaque pixels, and declaring that saves
                // Skia an unpremultiply pass on every frame.
                AlphaFormat.Opaque);
        }

        using var framebuffer = _bitmap.Lock();

        if (framebuffer.RowBytes == stride)
        {
            Marshal.Copy(buffer, 0, framebuffer.Address, stride * height);
            return;
        }

        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(
                buffer,
                y * stride,
                framebuffer.Address + (nint)y * framebuffer.RowBytes,
                stride);
        }
    }

    private void HandleResolutionChanged(int width, int height)
        => Dispatcher.UIThread.Post(InvalidateVisual);

    private void HandleStallTick(object? sender, EventArgs e)
    {
        if (!IsRendering)
            return;

        if (Environment.TickCount64 - _lastFrameTicks < StallTimeout.TotalMilliseconds)
            return;

        IsRendering = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bitmap = _bitmap;
        if (bitmap is null || !IsRendering)
            return;

        var bounds = Bounds.Size;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var (sourceRect, destinationRect) = ComputeRects(bitmap.Size, bounds, _stretch);
        if (destinationRect.Width <= 0 || destinationRect.Height <= 0)
            return;

        context.DrawImage(bitmap, sourceRect, destinationRect);
    }

    /// <summary>
    /// Works out which part of the picture to draw and where.
    /// <para>
    /// Cropping is expressed as a source rect rather than a clip so the GPU samples only
    /// the visible pixels.
    /// </para>
    /// </summary>
    internal static (Rect Source, Rect Destination) ComputeRects(
        Size sourceSize, Size bounds, Stretch stretch)
    {
        var full = new Rect(sourceSize);

        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
            return (full, new Rect(bounds));

        switch (stretch)
        {
            case Stretch.Fill:
                return (full, new Rect(bounds));

            case Stretch.UniformToFill:
            {
                // Keep the aspect ratio and fill the control, so trim the overflowing
                // dimension out of the source.
                var scale = Math.Max(
                    bounds.Width / sourceSize.Width, bounds.Height / sourceSize.Height);

                var visibleWidth = Math.Min(sourceSize.Width, bounds.Width / scale);
                var visibleHeight = Math.Min(sourceSize.Height, bounds.Height / scale);

                var source = new Rect(
                    (sourceSize.Width - visibleWidth) / 2,
                    (sourceSize.Height - visibleHeight) / 2,
                    visibleWidth,
                    visibleHeight);

                return (source, new Rect(bounds));
            }

            case Stretch.None:
            {
                var width = Math.Min(sourceSize.Width, bounds.Width);
                var height = Math.Min(sourceSize.Height, bounds.Height);

                var source = new Rect(
                    (sourceSize.Width - width) / 2, (sourceSize.Height - height) / 2, width, height);

                var destination = new Rect(
                    (bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height);

                return (source, destination);
            }

            default:
            {
                var scale = Math.Min(
                    bounds.Width / sourceSize.Width, bounds.Height / sourceSize.Height);

                var width = sourceSize.Width * scale;
                var height = sourceSize.Height * scale;

                var destination = new Rect(
                    (bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height);

                return (full, destination);
            }
        }
    }
}

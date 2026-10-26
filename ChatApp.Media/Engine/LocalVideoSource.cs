using ChatApp.Core.Media;

namespace ChatApp.Media.Engine;

/// <summary>
/// Our own capture, echoed to the UI as an <see cref="IVideoSource"/> so a local tile
/// renders through exactly the same path as a remote one.
/// <para>
/// Frames are taken straight from the capture backend rather than round-tripped through
/// the encoder: the preview then shows what the camera sees even when nothing is being
/// published, and it costs no extra decode.
/// </para>
/// </summary>
internal sealed class LocalVideoSource : IVideoSource
{
    public string Id { get; }
    public Guid UserId { get; }
    public string DisplayName { get; }
    public RemoteStreamKind Kind { get; }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsActive { get; private set; }

    public event VideoFrameHandler? FrameReady;
    public event Action<int, int>? ResolutionChanged;

    /// <summary>Frames handed over by capture, whether or not anything was listening.</summary>
    public long FramesPublished { get; private set; }

    /// <summary>
    /// Whether a renderer is attached. Separates "capture is not delivering" from "the
    /// tile never bound", which look identical from a blank preview.
    /// </summary>
    public bool HasListener => FrameReady is not null;

    public LocalVideoSource(string id, Guid userId, string displayName, RemoteStreamKind kind)
    {
        Id = id;
        UserId = userId;
        DisplayName = displayName;
        Kind = kind;
    }

    /// <summary>
    /// Publishes one captured frame. Called on the capture thread, inline with encoding,
    /// so handlers must return promptly.
    /// <para>
    /// The plane layout is described here rather than in a helper because
    /// <see cref="VideoFrame"/> is a ref struct: its spans may not outlive the frame they
    /// point at, so it has to be built and handed over in one place.
    /// </para>
    /// </summary>
    public void Publish(ReadOnlySpan<byte> data, int width, int height, VideoPixelFormat format)
    {
        if (data.IsEmpty || width <= 0 || height <= 0)
            return;

        // Counted before the listener check so a blank preview can be attributed to a
        // missing renderer rather than to capture.
        FramesPublished++;

        var handler = FrameReady;
        if (handler is null)
            return;

        if (width != Width || height != Height)
        {
            Width = width;
            Height = height;
            ResolutionChanged?.Invoke(width, height);
        }

        IsActive = true;

        Span<int> offsets = stackalloc int[3];
        Span<int> strides = stackalloc int[3];
        int planes;

        switch (format)
        {
            case VideoPixelFormat.Bgra32:
                planes = 1;
                offsets[0] = 0;
                strides[0] = width * 4;
                break;

            case VideoPixelFormat.Nv12:
                planes = 2;
                offsets[0] = 0;
                strides[0] = width;
                offsets[1] = width * height;
                strides[1] = width;
                break;

            default:
                planes = 3;
                var lumaSize = width * height;
                var chromaWidth = (width + 1) / 2;
                var chromaSize = chromaWidth * ((height + 1) / 2);

                offsets[0] = 0;
                strides[0] = width;
                offsets[1] = lumaSize;
                strides[1] = chromaWidth;
                offsets[2] = lumaSize + chromaSize;
                strides[2] = chromaWidth;
                break;
        }

        var frame = new VideoFrame
        {
            Width = width,
            Height = height,
            Format = format,
            Data = data,
            PlaneOffsets = offsets[..planes],
            PlaneStrides = strides[..planes],
            RtpTimestamp = 0
        };

        handler(this, in frame);
    }

    public void Stop() => IsActive = false;
}

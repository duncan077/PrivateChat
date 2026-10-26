using System.Buffers;

namespace ChatApp.Core.Media;

/// <summary>Pixel layout of a decoded frame handed to the renderer.</summary>
public enum VideoPixelFormat
{
    /// <summary>Planar Y/U/V, 4:2:0 — what every decoder in this app produces.</summary>
    I420,
    /// <summary>Semi-planar Y + interleaved UV, 4:2:0 — common from hardware decoders.</summary>
    Nv12,
    /// <summary>Packed 32bpp BGRA, straight alpha. Renderer-ready.</summary>
    Bgra32
}

/// <summary>
/// A decoded video frame. The buffer is owned by the producer and is only valid for
/// the duration of the <see cref="IVideoSource.FrameReady"/> callback — renderers must
/// copy or upload synchronously rather than retaining the memory.
/// </summary>
public readonly ref struct VideoFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required VideoPixelFormat Format { get; init; }

    /// <summary>Per-plane data. One entry for <see cref="VideoPixelFormat.Bgra32"/>, three for I420.</summary>
    public required ReadOnlySpan<byte> Data { get; init; }

    /// <summary>Byte offset of each plane within <see cref="Data"/>.</summary>
    public required ReadOnlySpan<int> PlaneOffsets { get; init; }

    /// <summary>Row stride of each plane, in bytes.</summary>
    public required ReadOnlySpan<int> PlaneStrides { get; init; }

    /// <summary>Capture/decode timestamp in 90 kHz RTP units.</summary>
    public required uint RtpTimestamp { get; init; }
}

/// <summary>
/// A source of decoded frames for one participant tile (local preview, a remote
/// camera, or a remote screen share). The UI attaches a renderer to this rather
/// than polling.
/// </summary>
public interface IVideoSource
{
    /// <summary>Stable key — <c>local</c>, or the Janus feed id for remote streams.</summary>
    string Id { get; }

    Guid UserId { get; }

    /// <summary>
    /// Label for the tile. Carried on the source because the publisher's name arrives
    /// with the Janus feed itself, well before the chat roster has necessarily caught up
    /// with a participant who just joined.
    /// </summary>
    string DisplayName { get; }

    RemoteStreamKind Kind { get; }

    int Width { get; }
    int Height { get; }

    bool IsActive { get; }

    /// <summary>
    /// Raised on the decode thread for every frame. Handlers must not block: the
    /// decoder stalls behind them and frames will be dropped.
    /// </summary>
    event VideoFrameHandler? FrameReady;

    /// <summary>Raised when <see cref="Width"/>/<see cref="Height"/> change mid-stream.</summary>
    event Action<int, int>? ResolutionChanged;
}

public delegate void VideoFrameHandler(IVideoSource source, in VideoFrame frame);

/// <summary>Exposes the engine's per-tile video sources to the UI layer.</summary>
public interface IVideoSourceProvider
{
    /// <summary>Our own camera, echoed back before encoding. Null while the camera is off.</summary>
    IVideoSource? LocalPreview { get; }

    /// <summary>What we are currently sharing. Null when not sharing.</summary>
    IVideoSource? LocalScreenShare { get; }

    IReadOnlyList<IVideoSource> RemoteSources { get; }

    /// <summary>
    /// Raised for local sources as well as remote ones, so the UI can treat every tile
    /// the same way. <see cref="RemoteSources"/> stays remote-only; the two local
    /// sources are reachable through their own properties.
    /// </summary>
    event Action<IVideoSource>? SourceAdded;

    event Action<IVideoSource>? SourceRemoved;
}

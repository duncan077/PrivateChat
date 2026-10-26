using ChatApp.Core.Media;
using FFmpeg.AutoGen;

namespace ChatApp.Media.Codec;

/// <summary>
/// swscale-backed <see cref="IVideoFrameConverter"/>: I420, NV12 or BGRA in, BGRA out,
/// scaled in the same pass to the size the renderer will actually draw.
/// <para>
/// swscale rather than a managed loop because it is SIMD-optimized per architecture and
/// FFmpeg is already loaded — a hand-written YUV→RGB pass over a 1080p frame costs more
/// than the decode that produced it.
/// </para>
/// </summary>
public sealed unsafe class SwsVideoFrameConverter : IVideoFrameConverter
{
    private readonly Lock _gate = new();
    private readonly byte*[] _srcData = new byte*[4];
    private readonly int[] _srcStride = new int[4];
    private readonly byte*[] _dstData = new byte*[4];
    private readonly int[] _dstStride = new int[4];

    private SwsContext* _scaler;
    private int _srcWidth;
    private int _srcHeight;
    private AVPixelFormat _srcFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private int _dstWidth;
    private int _dstHeight;
    private bool _disposed;

    /// <summary>Scaler currently in use, for diagnostics.</summary>
    public string Description => _scaler == null
        ? "idle"
        : $"{_srcWidth}x{_srcHeight} {_srcFormat} -> {_dstWidth}x{_dstHeight} bgra";

    public bool Convert(
        in VideoFrame frame,
        int targetWidth,
        int targetHeight,
        Span<byte> destination,
        int destinationStride)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || targetWidth <= 0 || targetHeight <= 0)
            return false;

        if (destinationStride < targetWidth * 4)
            return false;

        if (destination.Length < (long)destinationStride * targetHeight)
            return false;

        var sourceFormat = ToAvPixelFormat(frame.Format);
        var planes = PlaneCount(sourceFormat);

        if (frame.PlaneOffsets.Length < planes || frame.PlaneStrides.Length < planes)
            return false;

        lock (_gate)
        {
            if (_disposed)
                return false;

            EnsureScaler(frame.Width, frame.Height, sourceFormat, targetWidth, targetHeight);

            Array.Clear(_srcData);
            Array.Clear(_srcStride);
            Array.Clear(_dstData);
            Array.Clear(_dstStride);

            fixed (byte* src = frame.Data)
            fixed (byte* dst = destination)
            {
                for (var i = 0; i < planes; i++)
                {
                    _srcData[i] = src + frame.PlaneOffsets[i];
                    _srcStride[i] = frame.PlaneStrides[i];
                }

                _dstData[0] = dst;
                _dstStride[0] = destinationStride;

                var rows = ffmpeg.sws_scale(
                    _scaler, _srcData, _srcStride, 0, frame.Height, _dstData, _dstStride);

                return rows > 0;
            }
        }
    }

    private void EnsureScaler(
        int srcWidth,
        int srcHeight,
        AVPixelFormat srcFormat,
        int dstWidth,
        int dstHeight)
    {
        if (_scaler != null
            && _srcWidth == srcWidth
            && _srcHeight == srcHeight
            && _srcFormat == srcFormat
            && _dstWidth == dstWidth
            && _dstHeight == dstHeight)
        {
            return;
        }

        if (_scaler != null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        // Bilinear point-samples when shrinking, so a tile at less than half size drops
        // every other source pixel — screen shares full of text shimmer as they move.
        // Area averaging reads all of them and costs little at these sizes.
        var flags = dstWidth * 2 <= srcWidth
            ? (int)SwsFlags.SWS_AREA
            : (int)SwsFlags.SWS_BILINEAR;

        _scaler = ffmpeg.sws_getContext(
            srcWidth, srcHeight, srcFormat,
            dstWidth, dstHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
            flags, null, null, null);

        if (_scaler == null)
        {
            throw new InvalidOperationException(
                $"Could not create a scaler from {srcWidth}x{srcHeight} {srcFormat} " +
                $"to {dstWidth}x{dstHeight} bgra.");
        }

        _srcWidth = srcWidth;
        _srcHeight = srcHeight;
        _srcFormat = srcFormat;
        _dstWidth = dstWidth;
        _dstHeight = dstHeight;
    }

    private static AVPixelFormat ToAvPixelFormat(VideoPixelFormat format) => format switch
    {
        VideoPixelFormat.Nv12 => AVPixelFormat.AV_PIX_FMT_NV12,
        VideoPixelFormat.Bgra32 => AVPixelFormat.AV_PIX_FMT_BGRA,
        _ => AVPixelFormat.AV_PIX_FMT_YUV420P
    };

    private static int PlaneCount(AVPixelFormat format) => format switch
    {
        AVPixelFormat.AV_PIX_FMT_NV12 => 2,
        AVPixelFormat.AV_PIX_FMT_BGRA => 1,
        _ => 3
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_scaler != null)
            {
                ffmpeg.sws_freeContext(_scaler);
                _scaler = null;
            }
        }
    }
}

/// <summary>Hands the UI layer a converter without it having to know about FFmpeg.</summary>
public sealed class SwsVideoFrameConverterFactory : IVideoFrameConverterFactory
{
    public IVideoFrameConverter Create()
    {
        FFmpegLoader.EnsureAvailable();
        return new SwsVideoFrameConverter();
    }
}
